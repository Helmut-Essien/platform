using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public class RedisLicenseDenyListService(
    IDistributedCache cache,
    IServiceScopeFactory scopeFactory,
    IOptions<RedisSettings> settings,
    ILogger<RedisLicenseDenyListService> logger) : ILicenseDenyListService
{
    private static string DenyKey(string licenseId) => $"license:deny:{licenseId}";

    public async Task DenyLicenseAsync(string licenseId, CancellationToken cancellationToken = default)
    {
        try
        {
            await cache.SetStringAsync(
                DenyKey(licenseId),
                "1",
                new DistributedCacheEntryOptions(),
                cancellationToken);

            await InvalidateValidationCacheAsync(licenseId, cancellationToken);
            logger.LogDebug("License {LicenseId} added to deny-list", licenseId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Failed to write Redis deny-list for license {LicenseId}. Suspend/revoke still applied in the database.",
                licenseId);
        }
    }

    public async Task DenyCustomerLicensesAsync(string customerId, CancellationToken cancellationToken = default)
    {
        try
        {
            await cache.SetStringAsync(
                $"customer:deny:{customerId}",
                "1",
                new DistributedCacheEntryOptions(),
                cancellationToken);

            List<string> licenseIds;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                licenseIds = await db.Licenses
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(l => l.CustomerId == customerId)
                    .Select(l => l.Id)
                    .ToListAsync(cancellationToken);
            }

            foreach (var licenseId in licenseIds)
                await InvalidateValidationCacheAsync(licenseId, cancellationToken);

            logger.LogDebug("Customer {CustomerId} licenses marked denied in cache layer", customerId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Failed to write Redis customer deny-list for {CustomerId}. Customer suspension still applied in the database.",
                customerId);
        }
    }

    public async Task<bool> IsDeniedAsync(string licenseId, CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await cache.GetStringAsync(DenyKey(licenseId), cancellationToken);
            return value is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Failed to read Redis deny-list for license {LicenseId}; falling back to database status",
                licenseId);
            return await IsDeniedInDatabaseAsync(licenseId, cancellationToken);
        }
    }

    public async Task ClearLicenseDenyAsync(string licenseId, CancellationToken cancellationToken = default)
    {
        try
        {
            await cache.RemoveAsync(DenyKey(licenseId), cancellationToken);
            await InvalidateValidationCacheAsync(licenseId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to clear Redis deny-list for license {LicenseId}", licenseId);
        }
    }

    public async Task ClearCustomerDenyAsync(string customerId, CancellationToken cancellationToken = default)
    {
        try
        {
            await cache.RemoveAsync($"customer:deny:{customerId}", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to clear Redis customer deny-list for {CustomerId}", customerId);
        }
    }

    internal static string ValidationCacheKey(string serviceProductId, string lookupHash) =>
        $"license:valid:{serviceProductId}:{lookupHash}";

    internal static string ValidationCacheKeyByLicenseId(string licenseId) => $"license:valid:{licenseId}";

    internal int ValidationCacheSeconds => settings.Value.ValidationCacheSeconds;

    internal int IntegrationKeyLastUsedUpdateMinutes => settings.Value.IntegrationKeyLastUsedUpdateMinutes;

    private async Task<bool> IsDeniedInDatabaseAsync(string licenseId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var license = await db.Licenses
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(l => l.Id == licenseId)
            .Select(l => new { l.Status, CustomerSuspended = l.Customer.IsSuspended })
            .FirstOrDefaultAsync(cancellationToken);

        if (license is null)
            return true;

        return license.CustomerSuspended
            || license.Status is LicenseStatus.Suspended or LicenseStatus.Revoked or LicenseStatus.Pending;
    }

    private async Task InvalidateValidationCacheAsync(string licenseId, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(ValidationCacheKeyByLicenseId(licenseId), cancellationToken);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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
