namespace Platform.Api.Services;

public interface ILicenseDenyListService
{
    Task DenyLicenseAsync(string licenseId, CancellationToken cancellationToken = default);

    Task DenyCustomerLicensesAsync(string customerId, CancellationToken cancellationToken = default);

    Task<bool> IsDeniedAsync(string licenseId, CancellationToken cancellationToken = default);

    Task ClearLicenseDenyAsync(string licenseId, CancellationToken cancellationToken = default);

    Task ClearCustomerDenyAsync(string customerId, CancellationToken cancellationToken = default);
}
