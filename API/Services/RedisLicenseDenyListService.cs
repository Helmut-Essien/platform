using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;

namespace Platform.Api.Services;

public class RedisLicenseDenyListService(
    IDistributedCache cache,
    IOptions<RedisSettings> settings,
    ILogger<RedisLicenseDenyListService> logger) : ILicenseDenyListService
{
    private static string DenyKey(string licenseId) => $"license:deny:{licenseId}";

    public async Task DenyLicenseAsync(string licenseId, CancellationToken cancellationToken = default)
    {
        await cache.SetStringAsync(
            DenyKey(licenseId),
            "1",
            new DistributedCacheEntryOptions(),
            cancellationToken);

        await cache.RemoveAsync(ValidationCacheKey(licenseId), cancellationToken);
        logger.LogDebug("License {LicenseId} added to deny-list", licenseId);
    }

    public async Task DenyCustomerLicensesAsync(string customerId, CancellationToken cancellationToken = default)
    {
        await cache.SetStringAsync(
            $"customer:deny:{customerId}",
            "1",
            new DistributedCacheEntryOptions(),
            cancellationToken);

        logger.LogDebug("Customer {CustomerId} licenses marked denied in cache layer", customerId);
    }

    public async Task<bool> IsDeniedAsync(string licenseId, CancellationToken cancellationToken = default)
    {
        var value = await cache.GetStringAsync(DenyKey(licenseId), cancellationToken);
        return value is not null;
    }

    public async Task ClearLicenseDenyAsync(string licenseId, CancellationToken cancellationToken = default)
    {
        await cache.RemoveAsync(DenyKey(licenseId), cancellationToken);
        await cache.RemoveAsync(ValidationCacheKey(licenseId), cancellationToken);
    }

    public Task ClearCustomerDenyAsync(string customerId, CancellationToken cancellationToken = default) =>
        cache.RemoveAsync($"customer:deny:{customerId}");

    internal static string ValidationCacheKey(string licenseId) => $"license:valid:{licenseId}";

    internal int ValidationCacheSeconds => settings.Value.ValidationCacheSeconds;
}
