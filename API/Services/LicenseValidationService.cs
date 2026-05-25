using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Platform.Api.Data;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public class LicenseValidationService(
    AppDbContext db,
    ILicenseDenyListService denyList,
    IDistributedCache cache,
    ILogger<LicenseValidationService> logger) : ILicenseValidationService
{
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

        var licensesQuery = db.Licenses
            .IgnoreQueryFilters()
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .Where(l => l.ServiceProductId == serviceProduct.Id && l.LicenseKeyHash != null);

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

        if (matchedLicense.Customer.IsSuspended)
            return Invalid("Customer account is suspended.");

        if (matchedLicense.Status != LicenseStatus.Active)
            return Invalid($"License status is {matchedLicense.Status}.");

        if (matchedLicense.ExpiresAt.HasValue && matchedLicense.ExpiresAt.Value < DateTime.UtcNow)
            return Invalid("License has expired.");

        if (request.ServiceCode is not null &&
            !string.Equals(matchedLicense.ServiceProduct.Code, request.ServiceCode.Trim(), StringComparison.OrdinalIgnoreCase))
            return Invalid("License does not match the requested service.");

        integrationKey.LastUsedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var response = new ValidateLicenseResponse
        {
            IsValid = true,
            PlanName = matchedLicense.PlanName,
            ExpiresAt = matchedLicense.ExpiresAt
        };

        try
        {
            var cacheKey = RedisLicenseDenyListService.ValidationCacheKey(matchedLicense.Id);
            await cache.SetStringAsync(
                cacheKey,
                JsonSerializer.Serialize(response),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60)
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cache validation result for license {LicenseId}", matchedLicense.Id);
        }

        return response;
    }

    private async Task<(Entities.IntegrationKey Key, Entities.ServiceProduct Product)?> ResolveIntegrationKeyAsync(
        string plainIntegrationKey,
        string? serviceCode,
        CancellationToken cancellationToken)
    {
        IQueryable<Entities.IntegrationKey> query = db.IntegrationKeys
            .Include(k => k.ServiceProduct)
            .Where(k => k.IsActive);

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
