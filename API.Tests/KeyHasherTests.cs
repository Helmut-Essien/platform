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

public class KeyHasherTests
{
    [Fact]
    public void Hash_RoundTrips_AndIsSalted()
    {
        var a = KeyHasher.Hash("HOSTEL-AAAA-BBBB");
        var b = KeyHasher.Hash("HOSTEL-AAAA-BBBB");

        Assert.NotEqual(a, b);
        Assert.True(a.Length <= 100, "must fit the 100-char column");
        Assert.True(KeyHasher.Verify("HOSTEL-AAAA-BBBB", a, out var upgrade));
        Assert.False(upgrade);
        Assert.False(KeyHasher.Verify("HOSTEL-AAAA-BBBC", a, out _));
    }

    [Fact]
    public void Verify_AcceptsLegacyBCrypt_AndFlagsUpgrade()
    {
        var legacy = BCrypt.Net.BCrypt.HashPassword("legacy-key");

        Assert.True(KeyHasher.IsLegacy(legacy));
        Assert.True(KeyHasher.Verify("legacy-key", legacy, out var upgrade));
        Assert.True(upgrade);
        Assert.False(KeyHasher.Verify("other", legacy, out var upgradeWrong));
        Assert.False(upgradeWrong);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("v2$not-base64$also-bad")]
    [InlineData("v2$only-two")]
    public void Verify_RejectsMalformedHashes(string? stored)
    {
        Assert.False(KeyHasher.Verify("key", stored, out _));
    }

    [Fact]
    public async Task Validate_LegacyBCryptKeys_StillWork_AndAreUpgradedOnce()
    {
        const string licenseKey = "HOSTEL-LEGC-YKEY";
        const string integrationKey = "ik_legacy_integration_key";

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        var customer = new Customer { Name = "Legacy Ltd", ContactEmail = "legacy@acme.test" };
        var product = new ServiceProduct { Name = "Hostel", Code = "HOSTEL", IsAvailableForSale = true };
        var license = new License
        {
            Customer = customer,
            CustomerId = customer.Id,
            ServiceProduct = product,
            ServiceProductId = product.Id,
            Status = LicenseStatus.Active,
            PlanName = "Growth",
            ExpiresAt = DateTime.UtcNow.AddDays(10),
            LicenseKeyHash = BCrypt.Net.BCrypt.HashPassword(licenseKey),
            LicenseKeyLookupHash = KeyLookupHasher.ComputeSha256Hex(licenseKey)
        };
        var key = new IntegrationKey
        {
            ServiceProduct = product,
            ServiceProductId = product.Id,
            KeyHash = BCrypt.Net.BCrypt.HashPassword(integrationKey),
            KeyLookupHash = KeyLookupHasher.ComputeSha256Hex(integrationKey),
            IsActive = true
        };
        db.AddRange(customer, product, license, key);
        await db.SaveChangesAsync();

        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var service = new LicenseValidationService(
            db,
            new NoDenyList(),
            cache,
            Options.Create(new RedisSettings()),
            NullLogger<LicenseValidationService>.Instance);

        var result = await service.ValidateAsync(
            integrationKey,
            new ValidateLicenseRequest { LicenseKey = licenseKey, ServiceCode = "HOSTEL" });
        Assert.True(result.IsValid);

        await using var verifyDb = new AppDbContext(options);
        var storedLicense = await verifyDb.Licenses.IgnoreQueryFilters().SingleAsync();
        var storedKey = await verifyDb.IntegrationKeys.SingleAsync();
        Assert.False(KeyHasher.IsLegacy(storedLicense.LicenseKeyHash));
        Assert.False(KeyHasher.IsLegacy(storedKey.KeyHash));

        // Upgraded hashes keep working (fresh service, no cache).
        var service2 = new LicenseValidationService(
            verifyDb,
            new NoDenyList(),
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            Options.Create(new RedisSettings()),
            NullLogger<LicenseValidationService>.Instance);
        var again = await service2.ValidateAsync(
            integrationKey,
            new ValidateLicenseRequest { LicenseKey = licenseKey, ServiceCode = "HOSTEL" });
        Assert.True(again.IsValid);

        var wrong = await service2.ValidateAsync(
            integrationKey,
            new ValidateLicenseRequest { LicenseKey = "HOSTEL-WRNG-KEY1", ServiceCode = "HOSTEL" });
        Assert.False(wrong.IsValid);
    }

    private sealed class NoDenyList : ILicenseDenyListService
    {
        public Task DenyLicenseAsync(string licenseId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DenyCustomerLicensesAsync(string customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsDeniedAsync(string licenseId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ClearLicenseDenyAsync(string licenseId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearCustomerDenyAsync(string customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
