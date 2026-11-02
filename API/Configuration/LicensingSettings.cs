namespace Platform.Api.Configuration;

/// <summary>License validation, signed offline tokens and device activation ("Licensing" section).</summary>
public class LicensingSettings
{
    public const string SectionName = "Licensing";

    /// <summary>PKCS#8 / SEC1 PEM of an ECDSA P-256 private key (ES256). "\n" escapes are accepted for env vars.</summary>
    public string? TokenSigningKey { get; set; }

    /// <summary>Key id published in the JWKS and token header. Blank = RFC 7638 thumbprint of the key.</summary>
    public string? TokenSigningKeyId { get; set; }

    /// <summary>Older public keys still published in the JWKS during a rotation.</summary>
    public List<LicensingPublicKey> AdditionalPublicKeys { get; set; } = [];

    /// <summary>When true the app refuses to start without a valid signing key. Default false: tokens are omitted.</summary>
    public bool RequireTokenSigning { get; set; }

    public string TokenIssuer { get; set; } = "platform-license-hub";

    /// <summary>How long a product may run without reaching the server after a successful check (1–90).</summary>
    public int OfflineGraceDays { get; set; } = 7;

    /// <summary>Extra time past the license's own expiry the offline grace may reach (0–168). Default 0: never past expiry.</summary>
    public int ExpiryGraceHours { get; set; }

    /// <summary>When the product should check in again (1–168 hours, never later than half the grace window).</summary>
    public int TokenRefreshHours { get; set; } = 12;

    /// <summary>Device limit for newly issued licenses when the request does not set one. 0 = unlimited.</summary>
    public int DefaultMaxDevices { get; set; } = 3;

    /// <summary>When true, checks without a deviceId are refused (reason device_required). Default false for old clients.</summary>
    public bool RequireDeviceId { get; set; }

    /// <summary>LastSeen of a device is written at most this often (minutes).</summary>
    public int DeviceLastSeenUpdateMinutes { get; set; } = 60;

    /// <summary>Resolved integration keys are cached in memory this long (seconds, 0 = off). Revoking a key clears it.</summary>
    public int IntegrationKeyCacheSeconds { get; set; } = 60;

    /// <summary>Whole-check time budget before answering 503 (seconds).</summary>
    public int ValidationTimeoutSeconds { get; set; } = 10;

    /// <summary>Retry-After sent with 503 answers (seconds).</summary>
    public int UnavailableRetryAfterSeconds { get; set; } = 30;

    /// <summary>Checks per license key per window (all IPs). 0 = off.</summary>
    public int LicenseKeyPermitLimit { get; set; } = 30;

    /// <summary>Checks per client IP per window across all integration keys (stops key-rotation brute force). 0 = off.</summary>
    public int PerIpPermitLimit { get; set; } = 300;

    public int RateLimitWindowSeconds { get; set; } = 60;
}

public class LicensingPublicKey
{
    public string? Kid { get; set; }

    public string? PublicKeyPem { get; set; }
}
