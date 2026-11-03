namespace Platform.Shared.Dtos.Licenses;

public class ValidateLicenseResponse
{
    public bool IsValid { get; set; }

    public string? PlanName { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public string? Message { get; set; }

    /// <summary>Machine-readable outcome, see <see cref="LicenseValidationReasons"/> ("ok" when valid).</summary>
    public string? Reason { get; set; }

    /// <summary>Server clock (UTC) when the answer was produced. Clients use it to detect a wound-back local clock.</summary>
    public DateTime ServerTime { get; set; }

    public string? LicenseId { get; set; }

    /// <summary>Compact ES256 JWS the product can verify offline with the keys from GET /api/licenses/keys.
    /// Null when token signing is not configured on the server.</summary>
    public string? Token { get; set; }

    /// <summary>When the product should check in again (it may keep working offline until <see cref="OfflineGraceUntil"/>).</summary>
    public DateTime? RefreshAfter { get; set; }

    /// <summary>Latest moment the product may keep running on this answer without reaching the server.</summary>
    public DateTime? OfflineGraceUntil { get; set; }

    /// <summary>Device limit of the license (null = unlimited). Only set on device-bound checks.</summary>
    public int? MaxDevices { get; set; }

    /// <summary>Active devices on the license after this check. Only set on device-bound checks.</summary>
    public int? ActiveDevices { get; set; }

    /// <summary>True when the request carried an email and it matched the license's customer. Null when no email was sent.</summary>
    public bool? EmailMatches { get; set; }
}

/// <summary>Body of 429 / 503 answers from the validation endpoint (no isValid: the license was not judged).</summary>
public class ValidateLicenseErrorResponse
{
    public required string Reason { get; set; }

    public required string Message { get; set; }

    public DateTime ServerTime { get; set; }

    public int RetryAfterSeconds { get; set; }

    public bool Retryable { get; set; } = true;
}

public static class LicenseValidationReasons
{
    public const string Ok = "ok";
    public const string InvalidRequest = "invalid_request";
    public const string InvalidIntegrationKey = "invalid_integration_key";
    public const string InvalidKey = "invalid_key";
    public const string WrongService = "wrong_service";
    public const string NotActive = "not_active";
    public const string Expired = "expired";
    public const string Suspended = "suspended";
    public const string Revoked = "revoked";
    public const string DeviceRequired = "device_required";
    public const string DeviceLimit = "device_limit";
    public const string EmailMismatch = "email_mismatch";
    public const string RateLimited = "rate_limited";
    public const string ServiceUnavailable = "service_unavailable";
}
