using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Security;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class SeedOrderFlowTests
{
    [Fact]
    public async Task SeedOrderFlowAsync_AddsProductKeyAndDevLicenseWhenMissing()
    {
        await using var db = CreateDbContext();
        db.Customers.Add(new Customer
        {
            Name = "Existing Org",
            ContactEmail = "ops@example.com"
        });
        await db.SaveChangesAsync();

        await SeedData.SeedOrderFlowAsync(db, logger: null, isDevelopment: true);

        var product = await db.ServiceProducts.SingleAsync(p => p.Code == SeedData.OrderFlowServiceCode);
        Assert.Equal("OrderFlow", product.Name);

        var key = await db.IntegrationKeys.SingleAsync(k => k.ServiceProductId == product.Id && k.IsActive);
        Assert.True(BCrypt.Net.BCrypt.Verify(
            SeedData.DevIntegrationKeys[SeedData.OrderFlowServiceCode],
            key.KeyHash));

        var license = await db.Licenses.IgnoreQueryFilters()
            .SingleAsync(l => l.ServiceProductId == product.Id);
        Assert.Equal(LicenseStatus.Active, license.Status);
        Assert.Equal("Growth", license.PlanName);
        Assert.Equal(
            KeyLookupHasher.ComputeSha256Hex(SeedData.OrderFlowDevLicenseKey),
            license.LicenseKeyLookupHash);
        Assert.True(BCrypt.Net.BCrypt.Verify(SeedData.OrderFlowDevLicenseKey, license.LicenseKeyHash));
    }

    [Fact]
    public async Task SeedOrderFlowAsync_IsIdempotent()
    {
        await using var db = CreateDbContext();
        await SeedData.SeedOrderFlowAsync(db, logger: null, isDevelopment: true);
        await SeedData.SeedOrderFlowAsync(db, logger: null, isDevelopment: true);

        Assert.Equal(1, await db.ServiceProducts.CountAsync(p => p.Code == SeedData.OrderFlowServiceCode));
        Assert.Equal(1, await db.IntegrationKeys.CountAsync(k => k.IsActive));
        Assert.Equal(1, await db.Licenses.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task SeedOrderFlowAsync_SkipsDemoLicenseOutsideDevelopment()
    {
        await using var db = CreateDbContext();
        await SeedData.SeedOrderFlowAsync(db, logger: null, isDevelopment: false);

        Assert.True(await db.ServiceProducts.AnyAsync(p => p.Code == SeedData.OrderFlowServiceCode));
        Assert.False(await db.Licenses.IgnoreQueryFilters().AnyAsync());
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options;

        return new AppDbContext(options);
    }
}
