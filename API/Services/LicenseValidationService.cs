using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Security;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public class LicenseValidationService(
    AppDbContext db,
    ILicenseDenyListService denyList,
    IDistributedCache cache,
    IOptions<RedisSettings> redisSettings,
    ILogger<LicenseValidationService> logger) : ILicenseValidationService
{
    private readonly RedisSettings _redisSettings = redisSettings.Value;

    public async Task<ValidateLicenseResponse> ValidateAsync(
        string integrationKeyHeader,
        ValidateLicenseRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(integrationKeyHeader))
            return Invalid("Integration key is required.");

        if (string.IsNullOrWhiteSpace(request.LicenseKey))
            return Invalid("License key is required.");

        var integrationMatch = await ResolveIntegrationKeyAsync(integrationKeyHeader, request.ServiceCode, cancellationToken);
        if (integrationMatch is null)
            return Invalid("Invalid integration key.");

        var (integrationKey, serviceProduct) = integrationMatch.Value;
        var lookupHash = KeyLookupHasher.ComputeSha256Hex(request.LicenseKey);

        var cached = await TryGetCachedValidationAsync(
            serviceProduct.Id,
            lookupHash,
            cancellationToken);

        if (cached is not null)
        {
            await TouchIntegrationKeyLastUsedAsync(integrationKey.Id, integrationKey.LastUsedAt, cancellationToken);
            return cached;
        }

        var utcNow = DateTime.UtcNow;

        var licensesQuery = db.Licenses
            .IgnoreQueryFilters()
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .Where(l => l.ServiceProductId == serviceProduct.Id
                && l.LicenseKeyHash != null
                && l.Status == LicenseStatus.Active
                && !l.Customer.IsSuspended
                && (l.ExpiresAt == null || l.ExpiresAt > utcNow)
                && (l.LicenseKeyLookupHash == lookupHash || l.LicenseKeyLookupHash == null));

        var licenses = await licensesQuery.ToListAsync(cancellationToken);

        Entities.License? matchedLicense = null;
        foreach (var license in licenses)
        {
            if (license.LicenseKeyHash is not null &&
                BCrypt.Net.BCrypt.Verify(request.LicenseKey, license.LicenseKeyHash))
            {
                matchedLicense = license;
                break;
            }
        }

        if (matchedLicense is null)
            return Invalid("Invalid license key.");

        if (await denyList.IsDeniedAsync(matchedLicense.Id, cancellationToken))
            return Invalid("License is not valid.");

        var customerDenied = await cache.GetStringAsync($"customer:deny:{matchedLicense.CustomerId}", cancellationToken);
        if (customerDenied is not null)
            return Invalid("License is not valid.");

        if (request.ServiceCode is not null &&
            !string.Equals(matchedLicense.ServiceProduct.Code, request.ServiceCode.Trim(), StringComparison.OrdinalIgnoreCase))
            return Invalid("License does not match the requested service.");

        await TouchIntegrationKeyLastUsedAsync(integrationKey.Id, integrationKey.LastUsedAt, cancellationToken);

        var response = new ValidateLicenseResponse
        {
            IsValid = true,
            PlanName = matchedLicense.PlanName,
            ExpiresAt = matchedLicense.ExpiresAt
        };

        await CacheValidationAsync(
            serviceProduct.Id,
            lookupHash,
            matchedLicense.Id,
            matchedLicense.CustomerId,
            response,
            cancellationToken);

        return response;
    }

    private async Task<ValidateLicenseResponse?> TryGetCachedValidationAsync(
        string serviceProductId,
        string lookupHash,
        CancellationToken cancellationToken)
    {
        try
        {
            var cacheKey = RedisLicenseDenyListService.ValidationCacheKey(serviceProductId, lookupHash);
            var json = await cache.GetStringAsync(cacheKey, cancellationToken);
            if (json is null)
                return null;

            var entry = JsonSerializer.Deserialize<CachedLicenseValidation>(json);
            if (entry is null)
                return null;

            if (await denyList.IsDeniedAsync(entry.LicenseId, cancellationToken))
                return Invalid("License is not valid.");

            var customerDenied = await cache.GetStringAsync($"customer:deny:{entry.CustomerId}", cancellationToken);
            if (customerDenied is not null)
                return Invalid("License is not valid.");

            if (entry.Response.ExpiresAt.HasValue && entry.Response.ExpiresAt.Value < DateTime.UtcNow)
                return Invalid("License has expired.");

            return entry.Response;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read validation cache for service {ServiceProductId}", serviceProductId);
            return null;
        }
    }

    private async Task CacheValidationAsync(
        string serviceProductId,
        string lookupHash,
        string licenseId,
        string customerId,
        ValidateLicenseResponse response,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = new CachedLicenseValidation
            {
                LicenseId = licenseId,
                CustomerId = customerId,
                Response = response
            };

            var cacheKey = RedisLicenseDenyListService.ValidationCacheKey(serviceProductId, lookupHash);
            var ttl = TimeSpan.FromSeconds(_redisSettings.ValidationCacheSeconds);

            await cache.SetStringAsync(
                cacheKey,
                JsonSerializer.Serialize(entry),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cache validation result for license {LicenseId}", licenseId);
        }
    }

    private async Task TouchIntegrationKeyLastUsedAsync(
        string integrationKeyId,
        DateTime? lastUsedAt,
        CancellationToken cancellationToken)
    {
        var threshold = DateTime.UtcNow.AddMinutes(-_redisSettings.IntegrationKeyLastUsedUpdateMinutes);
        if (lastUsedAt.HasValue && lastUsedAt.Value >= threshold)
            return;

        try
        {
            await db.IntegrationKeys
                .Where(k => k.Id == integrationKeyId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(k => k.LastUsedAt, DateTime.UtcNow),
                    cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update LastUsedAt for integration key {IntegrationKeyId}", integrationKeyId);
        }
    }

    private async Task<(Entities.IntegrationKey Key, Entities.ServiceProduct Product)?> ResolveIntegrationKeyAsync(
        string plainIntegrationKey,
        string? serviceCode,
        CancellationToken cancellationToken)
    {
        var lookupHash = KeyLookupHasher.ComputeSha256Hex(plainIntegrationKey);

        IQueryable<Entities.IntegrationKey> query = db.IntegrationKeys
            .Include(k => k.ServiceProduct)
            .Where(k => k.IsActive
                && (k.KeyLookupHash == lookupHash || k.KeyLookupHash == null));

        if (!string.IsNullOrWhiteSpace(serviceCode))
            query = query.Where(k => k.ServiceProduct.Code == serviceCode.Trim().ToUpperInvariant());

        var candidates = await query.ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            if (BCrypt.Net.BCrypt.Verify(plainIntegrationKey, candidate.KeyHash))
                return (candidate, candidate.ServiceProduct);
        }

        return null;
    }

    private static ValidateLicenseResponse Invalid(string message) => new()
    {
        IsValid = false,
        Message = message
    };
}
