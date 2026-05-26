using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;

namespace Platform.Api.Services;

public class RedisLicenseDenyListService(
    IDistributedCache cache,
    AppDbContext db,
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

        await InvalidateValidationCacheAsync(licenseId, cancellationToken);
        logger.LogDebug("License {LicenseId} added to deny-list", licenseId);
    }

    public async Task DenyCustomerLicensesAsync(string customerId, CancellationToken cancellationToken = default)
    {
        await cache.SetStringAsync(
            $"customer:deny:{customerId}",
            "1",
            new DistributedCacheEntryOptions(),
            cancellationToken);

        var licenseIds = await db.Licenses
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(l => l.CustomerId == customerId)
            .Select(l => l.Id)
            .ToListAsync(cancellationToken);

        foreach (var licenseId in licenseIds)
            await InvalidateValidationCacheAsync(licenseId, cancellationToken);

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
        await InvalidateValidationCacheAsync(licenseId, cancellationToken);
    }

    public Task ClearCustomerDenyAsync(string customerId, CancellationToken cancellationToken = default) =>
        cache.RemoveAsync($"customer:deny:{customerId}");

    internal static string ValidationCacheKey(string serviceProductId, string lookupHash) =>
        $"license:valid:{serviceProductId}:{lookupHash}";

    internal static string ValidationCacheKeyByLicenseId(string licenseId) => $"license:valid:{licenseId}";

    internal int ValidationCacheSeconds => settings.Value.ValidationCacheSeconds;

    internal int IntegrationKeyLastUsedUpdateMinutes => settings.Value.IntegrationKeyLastUsedUpdateMinutes;

    private async Task InvalidateValidationCacheAsync(string licenseId, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(ValidationCacheKeyByLicenseId(licenseId), cancellationToken);

        var license = await db.Licenses
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(l => l.Id == licenseId)
            .Select(l => new { l.ServiceProductId, l.LicenseKeyLookupHash })
            .FirstOrDefaultAsync(cancellationToken);

        if (license?.LicenseKeyLookupHash is not null)
        {
            await cache.RemoveAsync(
                ValidationCacheKey(license.ServiceProductId, license.LicenseKeyLookupHash),
                cancellationToken);
        }
    }
}
