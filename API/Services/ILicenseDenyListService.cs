namespace Platform.Api.Services;

public interface ILicenseDenyListService
{
    Task DenyLicenseAsync(string licenseId, CancellationToken cancellationToken = default);

    Task DenyCustomerLicensesAsync(string customerId, CancellationToken cancellationToken = default);

    Task<bool> IsDeniedAsync(string licenseId, CancellationToken cancellationToken = default);

    Task ClearLicenseDenyAsync(string licenseId, CancellationToken cancellationToken = default);

    Task ClearCustomerDenyAsync(string customerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops a positive validation cache entry for a known lookup hash.
    /// Default no-op keeps test doubles source-compatible.
    /// </summary>
    Task InvalidateValidationCacheAsync(
        string serviceProductId,
        string lookupHash,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
