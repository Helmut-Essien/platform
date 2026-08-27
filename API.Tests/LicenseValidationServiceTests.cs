using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Security;
using Platform.Api.Services;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class LicenseValidationServiceTests
{
    private const string PlainLicenseKey = "HOSTEL-TEST-KEY1";
    private const string PlainIntegrationKey = "ik_test_integration_key_001";

    [Fact]
    public async Task ValidateAsync_ReturnsValidForActiveLicense()
    {
        await using var db = CreateDbContext();
        var fixture = await SeedActiveLicenseAsync(db);
        var service = CreateService(db, new FakeDenyList());

        var result = await service.ValidateAsync(
            PlainIntegrationKey,
            new ValidateLicenseRequest { LicenseKey = PlainLicenseKey, ServiceCode = "HOSTEL" });

        Assert.True(result.IsValid);
        Assert.Equal("Growth", result.PlanName);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsInvalidForBadLicenseKey()
    {
        await using var db = CreateDbContext();
        await SeedActiveLicenseAsync(db);
        var service = CreateService(db, new FakeDenyList());

        var result = await service.ValidateAsync(
            PlainIntegrationKey,
            new ValidateLicenseRequest { LicenseKey = "HOSTEL-WRONG-KEY1" });

        Assert.False(result.IsValid);
        Assert.Equal("Invalid license key.", result.Message);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsInvalidWhenLicenseOnDenyList()
    {
        await using var db = CreateDbContext();
        var fixture = await SeedActiveLicenseAsync(db);
        var denyList = new FakeDenyList { DeniedLicenseIds = { fixture.License.Id } };
        var service = CreateService(db, denyList);

        var result = await service.ValidateAsync(
            PlainIntegrationKey,
            new ValidateLicenseRequest { LicenseKey = PlainLicenseKey });

        Assert.False(result.IsValid);
        Assert.Equal("License is not valid.", result.Message);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsInvalidWhenCustomerSuspended()
    {
        await using var db = CreateDbContext();
        var fixture = await SeedActiveLicenseAsync(db);
        fixture.Customer.IsSuspended = true;
        await db.SaveChangesAsync();

        var service = CreateService(db, new FakeDenyList());

        var result = await service.ValidateAsync(
            PlainIntegrationKey,
            new ValidateLicenseRequest { LicenseKey = PlainLicenseKey });

        Assert.False(result.IsValid);
        Assert.Equal("Invalid license key.", result.Message);
    }

    [Fact]
    public async Task ValidateAsync_UsesCacheOnSecondCall()
    {
        await using var db = CreateDbContext();
        var fixture = await SeedActiveLicenseAsync(db);
        var cache = CreateCache();
        var service = CreateService(db, new FakeDenyList(), cache);

        var first = await service.ValidateAsync(
            PlainIntegrationKey,
            new ValidateLicenseRequest { LicenseKey = PlainLicenseKey });
        Assert.True(first.IsValid);

        // Break DB match so a cache miss would fail.
        fixture.License.LicenseKeyHash = BCrypt.Net.BCrypt.HashPassword("HOSTEL-OTHER-KEY1");
        fixture.License.LicenseKeyLookupHash = KeyLookupHasher.ComputeSha256Hex("HOSTEL-OTHER-KEY1");
        await db.SaveChangesAsync();

        var second = await service.ValidateAsync(
            PlainIntegrationKey,
            new ValidateLicenseRequest { LicenseKey = PlainLicenseKey });

        Assert.True(second.IsValid);
        Assert.Equal("Growth", second.PlanName);

        var cacheKey = RedisLicenseDenyListService.ValidationCacheKey(
            fixture.Product.Id,
            KeyLookupHasher.ComputeSha256Hex(PlainLicenseKey));
        Assert.NotNull(await cache.GetStringAsync(cacheKey));
    }

    [Fact]
    public async Task ValidateAsync_CacheHitStillHonorsDenyList()
    {
        await using var db = CreateDbContext();
        var fixture = await SeedActiveLicenseAsync(db);
        var denyList = new FakeDenyList();
        var service = CreateService(db, denyList);

        Assert.True((await service.ValidateAsync(
            PlainIntegrationKey,
            new ValidateLicenseRequest { LicenseKey = PlainLicenseKey })).IsValid);

        denyList.DeniedLicenseIds.Add(fixture.License.Id);

        var result = await service.ValidateAsync(
            PlainIntegrationKey,
            new ValidateLicenseRequest { LicenseKey = PlainLicenseKey });

        Assert.False(result.IsValid);
        Assert.Equal("License is not valid.", result.Message);
    }

    private static LicenseValidationService CreateService(
        AppDbContext db,
        ILicenseDenyListService denyList,
        IDistributedCache? cache = null)
    {
        return new LicenseValidationService(
            db,
            denyList,
            cache ?? CreateCache(),
            Options.Create(new RedisSettings { ValidationCacheSeconds = 60 }),
            NullLogger<LicenseValidationService>.Instance);
    }

    private static IDistributedCache CreateCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<(Customer Customer, ServiceProduct Product, License License)> SeedActiveLicenseAsync(
        AppDbContext db)
    {
        var customer = new Customer
        {
            Name = "Acme Ltd",
            ContactEmail = "owner@acme.test"
        };
        var product = new ServiceProduct
        {
            Name = "Hostel Manager",
            Code = "HOSTEL",
            IsAvailableForSale = true
        };
        var license = new License
        {
            Customer = customer,
            CustomerId = customer.Id,
            ServiceProduct = product,
            ServiceProductId = product.Id,
            Status = LicenseStatus.Active,
            PlanName = "Growth",
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            LicenseKeyHash = BCrypt.Net.BCrypt.HashPassword(PlainLicenseKey),
            LicenseKeyLookupHash = KeyLookupHasher.ComputeSha256Hex(PlainLicenseKey),
            LicenseKeySentAt = DateTime.UtcNow
        };
        var integrationKey = new IntegrationKey
        {
            ServiceProduct = product,
            ServiceProductId = product.Id,
            KeyHash = BCrypt.Net.BCrypt.HashPassword(PlainIntegrationKey),
            KeyLookupHash = KeyLookupHasher.ComputeSha256Hex(PlainIntegrationKey),
            IsActive = true
        };

        db.AddRange(customer, product, license, integrationKey);
        await db.SaveChangesAsync();
        return (customer, product, license);
    }

    private sealed class FakeDenyList : ILicenseDenyListService
    {
        public HashSet<string> DeniedLicenseIds { get; } = [];

        public Task DenyLicenseAsync(string licenseId, CancellationToken cancellationToken = default)
        {
            DeniedLicenseIds.Add(licenseId);
            return Task.CompletedTask;
        }

        public Task DenyCustomerLicensesAsync(string customerId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> IsDeniedAsync(string licenseId, CancellationToken cancellationToken = default) =>
            Task.FromResult(DeniedLicenseIds.Contains(licenseId));

        public Task ClearLicenseDenyAsync(string licenseId, CancellationToken cancellationToken = default)
        {
            DeniedLicenseIds.Remove(licenseId);
            return Task.CompletedTask;
        }

        public Task ClearCustomerDenyAsync(string customerId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
