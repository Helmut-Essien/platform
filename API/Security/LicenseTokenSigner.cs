using System.Buffers;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;

namespace Platform.Api.Security;

/// <summary>Claims of a signed license token (compact JWS, ES256).</summary>
public sealed record LicenseTokenClaims(
    string LicenseId,
    string CustomerId,
    string Product,
    string Plan,
    DateTime? LicenseExpiresAt,
    string? DeviceId,
    bool Trial,
    bool Complimentary,
    DateTime IssuedAt,
    DateTime RefreshAfter,
    DateTime GraceUntil);

public sealed record LicenseJwk(string Kid, string X, string Y);

/// <summary>
/// Signs license tokens with an ECDSA P-256 key loaded once at startup (singleton). Tokens are standard compact JWS
/// (header.payload.signature, base64url, ES256 = raw r||s), so any JWT library can verify them with the JWKS.
/// </summary>
public sealed class LicenseTokenSigner : IDisposable
{
    public const string Algorithm = "ES256";
    public const string TokenType = "license+jwt";

    private readonly ECDsa? _key;
    private readonly byte[]? _headerSegment;
    private readonly string _issuer;
    private readonly Lock _signLock = new();

    public LicenseTokenSigner(IOptions<LicensingSettings> options, IHostEnvironment environment, ILogger<LicenseTokenSigner> logger)
    {
        var settings = options.Value;
        _issuer = string.IsNullOrWhiteSpace(settings.TokenIssuer) ? "platform-license-hub" : settings.TokenIssuer.Trim();
        var keys = new List<LicenseJwk>();

        var pem = NormalizePem(settings.TokenSigningKey);
        if (pem is not null)
        {
            try
            {
                _key = LoadPrivateKey(pem);
            }
            catch (InvalidOperationException ex) when (!settings.RequireTokenSigning)
            {
                // A bad key must not take the whole site down: run without signed tokens and shout about it.
                logger.LogError(ex,
                    "LICENSE TOKENS DISABLED: Licensing__TokenSigningKey could not be loaded ({Reason}). /api/licenses/validate answers without a signed offline token. " +
                    "Regenerate with 'dotnet Platform.Api.dll generate-license-key' and paste the PRIVATE KEY PEM (\\n escapes allowed).", ex.Message);
            }
            if (_key is not null)
            {
                ActiveKid = string.IsNullOrWhiteSpace(settings.TokenSigningKeyId) ? Thumbprint(_key) : settings.TokenSigningKeyId.Trim();
                keys.Add(ToJwk(ActiveKid, _key));
            }
        }
        else if (settings.RequireTokenSigning)
        {
            throw new InvalidOperationException(
                "Licensing:TokenSigningKey is required (Licensing:RequireTokenSigning=true). Generate one with: dotnet Platform.Api.dll generate-license-key");
        }
        else if (environment.IsDevelopment())
        {
            _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ActiveKid = "dev-" + Thumbprint(_key)[..12];
            keys.Add(ToJwk(ActiveKid, _key));
            logger.LogWarning("Licensing:TokenSigningKey is not set: using a throw-away development signing key ({Kid}). Tokens will not verify after a restart.", ActiveKid);
        }
        else
        {
            logger.LogError(
                "LICENSE TOKENS DISABLED: Licensing__TokenSigningKey is not set, so /api/licenses/validate answers without a signed offline token. " +
                "Products cannot run offline. Generate a key with 'dotnet Platform.Api.dll generate-license-key' (see docs/DEPLOY-MONSTERASP.md).");
        }

        foreach (var extra in settings.AdditionalPublicKeys)
        {
            var extraPem = NormalizePem(extra.PublicKeyPem);
            if (extraPem is null)
                continue;
            ECDsa pubKey;
            try
            {
                pubKey = LoadPublicKey(extraPem);
            }
            catch (InvalidOperationException ex)
            {
                // An old rotation key that won't parse only affects tokens signed before the rotation: log, keep serving.
                logger.LogError(ex, "Licensing:AdditionalPublicKeys entry '{Kid}' was skipped: {Reason}", extra.Kid, ex.Message);
                continue;
            }
            using var pub = pubKey;
            var kid = string.IsNullOrWhiteSpace(extra.Kid) ? Thumbprint(pub) : extra.Kid.Trim();
            if (keys.All(k => k.Kid != kid))
                keys.Add(ToJwk(kid, pub));
        }

        PublicKeys = keys;

        if (_key is not null)
        {
            _headerSegment = Encoding.ASCII.GetBytes(Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
            {
                ["alg"] = Algorithm,
                ["typ"] = TokenType,
                ["kid"] = ActiveKid!
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
        }
    }

    public bool CanSign => _key is not null;

    public string? ActiveKid { get; }

    public IReadOnlyList<LicenseJwk> PublicKeys { get; }

    public string? Sign(LicenseTokenClaims claims)
    {
        if (_key is null || _headerSegment is null)
            return null;

        var payload = Base64Url(WritePayload(claims));
        var signingInput = new byte[_headerSegment.Length + 1 + payload.Length];
        _headerSegment.CopyTo(signingInput, 0);
        signingInput[_headerSegment.Length] = (byte)'.';
        Encoding.ASCII.GetBytes(payload, 0, payload.Length, signingInput, _headerSegment.Length + 1);

        byte[] signature;
        lock (_signLock) // ECDsa instances are not documented as thread-safe; signing takes ~50 µs.
            signature = _key.SignData(signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return string.Concat(Encoding.ASCII.GetString(signingInput), ".", Base64Url(signature));
    }

    private byte[] WritePayload(LicenseTokenClaims c)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("iss", _issuer);
            w.WriteString("sub", c.LicenseId);
            w.WriteString("aud", c.Product);
            w.WriteNumber("iat", ToUnix(c.IssuedAt));
            w.WriteNumber("exp", ToUnix(c.GraceUntil));
            w.WriteString("license_id", c.LicenseId);
            w.WriteString("customer_id", c.CustomerId);
            w.WriteString("product", c.Product);
            w.WriteString("plan", c.Plan);
            if (c.LicenseExpiresAt is { } exp)
                w.WriteNumber("license_expires_at", ToUnix(exp));
            else
                w.WriteNull("license_expires_at");
            if (c.DeviceId is not null)
                w.WriteString("device_id", c.DeviceId);
            w.WriteBoolean("trial", c.Trial);
            w.WriteBoolean("complimentary", c.Complimentary);
            w.WriteNumber("server_time", ToUnix(c.IssuedAt));
            w.WriteNumber("refresh_after", ToUnix(c.RefreshAfter));
            w.WriteNumber("grace_until", ToUnix(c.GraceUntil));
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Creates a new P-256 key pair: (private PKCS#8 PEM, public SPKI PEM, kid).</summary>
    public static (string PrivatePem, string PublicPem, string Kid) GenerateKeyPair()
    {
        // Encoded by hand (not ExportPkcs8PrivateKeyPem) so the command never touches CNG PKCS#8 key storage on Windows.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(includePrivateParameters: true);
        var point = new byte[65];
        point[0] = 0x04;
        p.Q.X!.CopyTo(point, 1);
        p.Q.Y!.CopyTo(point, 33);

        var ec = new AsnWriter(AsnEncodingRules.DER);
        using (ec.PushSequence())
        {
            ec.WriteInteger(1);
            ec.WriteOctetString(p.D);
            using (ec.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1, true)))
                ec.WriteBitString(point);
        }
        var pkcs8 = new AsnWriter(AsnEncodingRules.DER);
        using (pkcs8.PushSequence())
        {
            pkcs8.WriteInteger(0);
            using (pkcs8.PushSequence())
            {
                pkcs8.WriteObjectIdentifier(EcPublicKeyOid.Value!);
                pkcs8.WriteObjectIdentifier(P256Oid);
            }
            pkcs8.WriteOctetString(ec.Encode());
        }
        var spki = new AsnWriter(AsnEncodingRules.DER);
        using (spki.PushSequence())
        {
            using (spki.PushSequence())
            {
                spki.WriteObjectIdentifier(EcPublicKeyOid.Value!);
                spki.WriteObjectIdentifier(P256Oid);
            }
            spki.WriteBitString(point);
        }
        CryptographicOperations.ZeroMemory(p.D);
        return (new string(PemEncoding.Write("PRIVATE KEY", pkcs8.Encode())) + "\n",
                new string(PemEncoding.Write("PUBLIC KEY", spki.Encode())) + "\n",
                Thumbprint(key));
    }

    internal static long ToUnix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    internal static string? NormalizePem(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim().Replace("\\n", "\n", StringComparison.Ordinal);
    }

    // IMPORTANT: never use ECDsa.ImportFromPem / ImportPkcs8PrivateKey / ImportSubjectPublicKeyInfo here. On Windows
    // they go through CNG's PKCS#8 import, which needs a loaded user profile; IIS app pools (MonsterASP) don't have
    // one and fail with "The system cannot find the file specified". We decode the DER ourselves and import explicit
    // ECParameters, which creates an ephemeral key and works everywhere.
    private static readonly Oid EcPublicKeyOid = new("1.2.840.10045.2.1");
    private const string P256Oid = "1.2.840.10045.3.1.7";

    internal static ECDsa LoadPrivateKey(string pem)
    {
        const string setting = "Licensing:TokenSigningKey";
        try
        {
            var (label, der) = DecodePem(pem, setting);
            ECParameters p = label switch
            {
                "PRIVATE KEY" => ReadPkcs8(der, setting),
                "EC PRIVATE KEY" => ReadSec1(der, null, setting),
                _ => throw new InvalidOperationException($"{setting} must be a 'PRIVATE KEY' (PKCS#8) or 'EC PRIVATE KEY' PEM, not '{label}'."),
            };
            var key = ECDsa.Create(p);
            // Round-trip sanity check: the key must sign and its own public half must verify.
            var probe = new byte[] { 1, 2, 3 };
            if (!key.VerifyData(probe, key.SignData(probe, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256))
            {
                key.Dispose();
                throw new InvalidOperationException($"{setting}: the public key does not match the private key.");
            }
            return key;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or AsnContentException or FormatException)
        {
            throw new InvalidOperationException($"{setting} is not a valid EC P-256 private key PEM: {ex.Message}", ex);
        }
    }

    internal static ECDsa LoadPublicKey(string pem)
    {
        const string setting = "Licensing:AdditionalPublicKeys";
        try
        {
            var (label, der) = DecodePem(pem, setting);
            if (label != "PUBLIC KEY")
                throw new InvalidOperationException($"{setting} must be a 'PUBLIC KEY' PEM, not '{label}'.");
            var spki = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
            var alg = spki.ReadSequence();
            ReadAlgorithm(alg, setting);
            var point = spki.ReadBitString(out _);
            return ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = DecodePoint(point, setting) });
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or AsnContentException or FormatException)
        {
            throw new InvalidOperationException($"{setting} contains an invalid EC P-256 public key PEM: {ex.Message}", ex);
        }
    }

    private static (string Label, byte[] Der) DecodePem(string pem, string setting)
    {
        var text = NormalizePem(pem) ?? throw new InvalidOperationException($"{setting} is empty.");
        text = text.Replace("\\r", "", StringComparison.Ordinal).Trim('"', '\'', ' ', '\r', '\n', '\t');
        var begin = text.IndexOf("-----BEGIN ", StringComparison.Ordinal);
        if (begin < 0)
            throw new InvalidOperationException($"{setting} has no '-----BEGIN ...-----' line.");
        var labelStart = begin + "-----BEGIN ".Length;
        var labelEnd = text.IndexOf("-----", labelStart, StringComparison.Ordinal);
        if (labelEnd < 0)
            throw new InvalidOperationException($"{setting} has a malformed BEGIN line.");
        var label = text[labelStart..labelEnd].Trim();
        var footer = "-----END " + label + "-----";
        var end = text.IndexOf(footer, labelEnd, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException($"{setting} has no matching '{footer}' line.");
        var body = text[(labelEnd + 5)..end];
        var sb = new StringBuilder(body.Length);
        foreach (var ch in body)
            if (!char.IsWhiteSpace(ch))
                sb.Append(ch);
        return (label, Convert.FromBase64String(sb.ToString()));
    }

    private static void ReadAlgorithm(AsnReader alg, string setting)
    {
        if (alg.ReadObjectIdentifier() != EcPublicKeyOid.Value)
            throw new InvalidOperationException($"{setting} is not an EC key.");
        if (alg.ReadObjectIdentifier() != P256Oid)
            throw new InvalidOperationException($"{setting} must be an ECDSA P-256 (prime256v1) key for ES256.");
    }

    private static ECParameters ReadPkcs8(byte[] der, string setting)
    {
        var info = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
        info.ReadInteger(); // version
        ReadAlgorithm(info.ReadSequence(), setting);
        return ReadSec1(info.ReadOctetString(), checkedCurve: true, setting);
    }

    /// <summary>RFC 5915 ECPrivateKey: SEQUENCE { version, privateKey OCTET STRING, [0] curve OID?, [1] publicKey BIT STRING? }.</summary>
    private static ECParameters ReadSec1(byte[] der, bool? checkedCurve, string setting)
    {
        var seq = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
        if (seq.ReadInteger() != 1)
            throw new InvalidOperationException($"{setting}: unsupported ECPrivateKey version.");
        var d = seq.ReadOctetString();
        if (d.Length > 32)
            throw new InvalidOperationException($"{setting} must be an ECDSA P-256 key for ES256.");
        if (d.Length < 32)
            d = new byte[32 - d.Length].Concat(d).ToArray();
        var curveSeen = checkedCurve == true;
        ECPoint? q = null;
        var ctx0 = new Asn1Tag(TagClass.ContextSpecific, 0, true);
        var ctx1 = new Asn1Tag(TagClass.ContextSpecific, 1, true);
        if (seq.HasData && seq.PeekTag().HasSameClassAndValue(ctx0))
        {
            var oid = seq.ReadSequence(ctx0).ReadObjectIdentifier();
            if (oid != P256Oid)
                throw new InvalidOperationException($"{setting} must be an ECDSA P-256 (prime256v1) key for ES256.");
            curveSeen = true;
        }
        if (seq.HasData && seq.PeekTag().HasSameClassAndValue(ctx1))
            q = DecodePoint(seq.ReadSequence(ctx1).ReadBitString(out _), setting);
        if (!curveSeen)
            throw new InvalidOperationException($"{setting}: the EC PRIVATE KEY does not name its curve; use the PKCS#8 'PRIVATE KEY' form.");
        // Without Q, .NET derives the public point from D during the explicit-parameter import.
        return new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d, Q = q ?? default };
    }

    private static ECPoint DecodePoint(byte[] point, string setting)
    {
        if (point.Length != 65 || point[0] != 0x04)
            throw new InvalidOperationException($"{setting}: expected an uncompressed P-256 public point.");
        return new ECPoint { X = point[1..33], Y = point[33..65] };
    }

    private static LicenseJwk ToJwk(string kid, ECDsa key)
    {
        var p = key.ExportParameters(includePrivateParameters: false);
        return new LicenseJwk(kid, Base64Url(p.Q.X!), Base64Url(p.Q.Y!));
    }

    /// <summary>RFC 7638 JWK thumbprint (SHA-256, base64url).</summary>
    internal static string Thumbprint(ECDsa key)
    {
        var p = key.ExportParameters(includePrivateParameters: false);
        var json = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{Base64Url(p.Q.X!)}\",\"y\":\"{Base64Url(p.Q.Y!)}\"}}";
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(json)));
    }

    internal static string Base64Url(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _key?.Dispose();
}
