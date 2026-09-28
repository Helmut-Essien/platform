using Platform.Shared.Dtos.Licenses;

namespace Platform.Api.Services;

internal sealed class CachedLicenseValidation
{
    public required string LicenseId { get; init; }

    public required string CustomerId { get; init; }

    /// <summary>
    /// Lookup hash of the key that was validated. Cache hits whose hash no longer
    /// matches the license are discarded so a rotated key cannot stay valid.
    /// </summary>
    public string? LookupHash { get; init; }

    public required ValidateLicenseResponse Response { get; init; }
}
