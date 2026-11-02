using System.Buffers;
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
            _key = LoadPrivateKey(pem);
            ActiveKid = string.IsNullOrWhiteSpace(settings.TokenSigningKeyId) ? Thumbprint(_key) : settings.TokenSigningKeyId.Trim();
            keys.Add(ToJwk(ActiveKid, _key));
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
            using var pub = ECDsa.Create();
            try
            {
                pub.ImportFromPem(extraPem);
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                throw new InvalidOperationException("Licensing:AdditionalPublicKeys contains an invalid PEM public key.", ex);
            }
            EnsureP256(pub, "Licensing:AdditionalPublicKeys");
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
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem(), Thumbprint(key));
    }

    internal static long ToUnix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    internal static string? NormalizePem(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim().Replace("\\n", "\n", StringComparison.Ordinal);
    }

    private static ECDsa LoadPrivateKey(string pem)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            // Export fails for a public-only key: make sure we really got a private key.
            key.ExportParameters(includePrivateParameters: true);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            key.Dispose();
            throw new InvalidOperationException("Licensing:TokenSigningKey is not a valid EC private key PEM.", ex);
        }
        EnsureP256(key, "Licensing:TokenSigningKey");
        return key;
    }

    private static void EnsureP256(ECDsa key, string setting)
    {
        if (key.KeySize != 256)
            throw new InvalidOperationException($"{setting} must be an ECDSA P-256 (prime256v1) key for ES256.");
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
