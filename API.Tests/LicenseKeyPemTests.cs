using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Security;
using Xunit;

namespace API.Tests;

/// <summary>Key loading must never use CNG PKCS#8 import (fails on IIS app pools without a user profile).</summary>
public class LicenseKeyPemTests
{
    // Test-only keys generated with openssl (prime256v1); not used anywhere else.
    private const string OpenSslPkcs8 = """
        -----BEGIN PRIVATE KEY-----
        MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg1UlYYy0itJidQEar
        M+bpB0ALELdsdW9w5mhv4mDKAwOhRANCAAQ4qbWITDbkUf8v50a1qPDCrqgQI3Rz
        Q2ZQoLXyNUw9T7o6MjCsuRqEm80XpaREGx/mMhoLMOQWBrGLqeGdnc7g
        -----END PRIVATE KEY-----
        """;
    private const string OpenSslSec1 = """
        -----BEGIN EC PRIVATE KEY-----
        MHcCAQEEINVJWGMtIrSYnUBGqzPm6QdACxC3bHVvcOZob+JgygMDoAoGCCqGSM49
        AwEHoUQDQgAEOKm1iEw25FH/L+dGtajwwq6oECN0c0NmUKC18jVMPU+6OjIwrLka
        hJvNF6WkRBsf5jIaCzDkFgaxi6nhnZ3O4A==
        -----END EC PRIVATE KEY-----
        """;
    private const string OpenSslSec1NoPublic = """
        -----BEGIN EC PRIVATE KEY-----
        MDECAQEEINVJWGMtIrSYnUBGqzPm6QdACxC3bHVvcOZob+JgygMDoAoGCCqGSM49
        AwEH
        -----END EC PRIVATE KEY-----
        """;
    private const string OpenSslPublic = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOKm1iEw25FH/L+dGtajwwq6oECN0
        c0NmUKC18jVMPU+6OjIwrLkahJvNF6WkRBsf5jIaCzDkFgaxi6nhnZ3O4A==
        -----END PUBLIC KEY-----
        """;
    private const string OpenSslP384 = """
        -----BEGIN PRIVATE KEY-----
        MIG2AgEAMBAGByqGSM49AgEGBSuBBAAiBIGeMIGbAgEBBDAYzbSPgrhL47P9BGMx
        1ZLpAcXwn+ffkmNlvDVkQ1PwVzzzrPuadcmR2EEME4LLc1ShZANiAAQ2juJ660tk
        9Tw4pa6ZepCdatPzbYgBHeIIprZ8S/7at1S/RLTlth4eD4tJeDNxrEls1jDCv9sU
        aD76u0tjEliIY8MKDyYOW5LClMZ2qK/2u8apCNpoFxc4zBMnQJSzh5Y=
        -----END PRIVATE KEY-----
        """;

    private static LicenseTokenSigner Signer(LicensingSettings s) =>
        new(Options.Create(s), new LicenseValidationTestFactory.TestEnv("Production"), NullLogger<LicenseTokenSigner>.Instance);

    private static string Kid(string pem) => Signer(new LicensingSettings { TokenSigningKey = pem }).ActiveKid!;

    [Fact]
    public void OpenSsl_Pkcs8_Sec1_Sec1WithoutPublic_AndPublic_AllGiveSameKey()
    {
        var kid = Kid(OpenSslPkcs8);
        Assert.Equal(kid, Kid(OpenSslSec1));
        Assert.Equal(kid, Kid(OpenSslSec1NoPublic));
        using var pub = LicenseTokenSigner.LoadPublicKey(OpenSslPublic);
        Assert.Equal(kid, LicenseTokenSigner.Thumbprint(pub));
        Assert.True(Signer(new LicensingSettings { TokenSigningKey = OpenSslPkcs8 }).CanSign);
    }

    [Theory]
    [InlineData("crlf")]
    [InlineData("escaped")]
    [InlineData("escapedCrlf")]
    [InlineData("quoted")]
    public void GeneratedKey_LoadsInEveryPastedForm(string form)
    {
        var (priv, pub, kid) = LicenseTokenSigner.GenerateKeyPair();
        var value = form switch
        {
            "crlf" => priv.Replace("\n", "\r\n"),
            "escaped" => priv.TrimEnd().Replace("\n", "\\n"),
            "escapedCrlf" => priv.TrimEnd().Replace("\n", "\\r\\n"),
            _ => "\"" + priv.TrimEnd().Replace("\n", "\\n") + "\"",
        };
        Assert.Equal(kid, Kid(value));
        using var p = LicenseTokenSigner.LoadPublicKey(pub);
        Assert.Equal(kid, LicenseTokenSigner.Thumbprint(p));
    }

    [Theory]
    [InlineData("not a pem")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----")]
    [InlineData("-----BEGIN PRIVATE KEY-----\n!!notbase64!!\n-----END PRIVATE KEY-----")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nMIGHAgEAMBMG")]
    public void BadKey_DoesNotCrash_RunsWithoutSigning_UnlessRequired(string pem)
    {
        var s = Signer(new LicensingSettings { TokenSigningKey = pem });
        Assert.False(s.CanSign);
        Assert.Empty(s.PublicKeys);
        Assert.Throws<InvalidOperationException>(() =>
            Signer(new LicensingSettings { TokenSigningKey = pem, RequireTokenSigning = true }));
    }

    [Fact]
    public void WrongCurve_PublicKeyGiven_AndBadRotationKey_AreHandled()
    {
        Assert.False(Signer(new LicensingSettings { TokenSigningKey = OpenSslP384 }).CanSign);
        Assert.False(Signer(new LicensingSettings { TokenSigningKey = OpenSslPublic }).CanSign);
        var s = Signer(new LicensingSettings
        {
            TokenSigningKey = OpenSslPkcs8,
            AdditionalPublicKeys = [new() { Kid = "old", PublicKeyPem = "garbage" }, new() { Kid = "old2", PublicKeyPem = OpenSslPublic.Replace("\n", "\\n") }]
        });
        Assert.True(s.CanSign);
        Assert.Equal(2, s.PublicKeys.Count); // the garbage rotation key is skipped, not fatal
        Assert.Contains(s.PublicKeys, k => k.Kid == "old2");
        Assert.DoesNotContain(s.PublicKeys, k => k.Kid == "old");
    }
}
