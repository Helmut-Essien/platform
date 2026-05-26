using Platform.Shared.Dtos.Licenses;

namespace Platform.Api.Services;

internal sealed class CachedLicenseValidation
{
    public required string LicenseId { get; init; }

    public required string CustomerId { get; init; }

    public required ValidateLicenseResponse Response { get; init; }
}
