using Platform.Shared.Dtos.Licenses;

namespace Platform.Api.Services;

public interface ILicenseValidationService
{
    Task<ValidateLicenseResponse> ValidateAsync(
        string integrationKeyHeader,
        ValidateLicenseRequest request,
        CancellationToken cancellationToken = default);
}
