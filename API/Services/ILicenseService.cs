using Platform.Shared.Dtos.Licenses;

namespace Platform.Api.Services;

public interface ILicenseService
{
    Task<LicenseDto> CreateAsync(CreateLicenseRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LicenseDto>> ListAsync(string? customerId = null, bool includeSuspendedCustomers = false, CancellationToken cancellationToken = default);

    Task<LicenseDto?> GetAsync(string id, bool includeSuspendedCustomers = false, CancellationToken cancellationToken = default);

    Task<LicenseDto> UpdateAsync(string id, UpdateLicenseRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    Task<LicenseDto> SuspendAsync(string id, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    Task<LicenseDto> RevokeAsync(string id, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    Task<LicenseDto> ActivateAsync(string id, ActivateLicenseRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    Task<LicenseDto> RenewAsync(string id, RenewLicenseRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);
}
