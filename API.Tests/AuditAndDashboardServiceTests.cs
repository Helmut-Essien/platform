using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class AuditAndDashboardServiceTests
{
    [Fact]
    public async Task SearchAsync_PagesAndFiltersOnTheServerAndResolvesNames()
    {
        // E6
        await using var db = CreateDb();
        var acme = new Customer { Name = "Acme Ltd", ContactEmail = "a@acme.test" };
        var globex = new Customer { Name = "Globex", ContactEmail = "g@globex.test" };
        db.Customers.AddRange(acme, globex);
        for (var i = 0; i < 60; i++)
        {
            db.AuditLogs.Add(new AuditLog
            {
                Action = i % 2 == 0 ? AuditAction.LicenseIssued : AuditAction.InvoiceVoided,
                PerformedBy = "admin@example.com",
                CustomerId = i % 3 == 0 ? globex.Id : acme.Id,
                IpAddress = "10.0.0." + i,
                Timestamp = DateTime.UtcNow.AddMinutes(-i)
            });
        }

        await db.SaveChangesAsync();
        var service = new AuditLogService(db);

        var first = await service.SearchAsync(page: 1, pageSize: 25);
        var third = await service.SearchAsync(page: 3, pageSize: 25);
        Assert.Equal(60, first.TotalCount);
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(10, third.Items.Count);
        Assert.True(first.Items[0].Timestamp >= first.Items[1].Timestamp);
        Assert.Equal(first.Items.Count, first.Items.Select(i => i.Id).Distinct().Count());
        Assert.All(first.Items, i => Assert.False(string.IsNullOrEmpty(i.CustomerName)));

        var voided = await service.SearchAsync(action: AuditAction.InvoiceVoided, pageSize: 100);
        Assert.Equal(30, voided.TotalCount);

        var byCustomerName = await service.SearchAsync(search: "globex", pageSize: 100);
        Assert.Equal(20, byCustomerName.TotalCount);

        var byActionText = await service.SearchAsync(search: "INVOICE_VOIDED", pageSize: 100);
        Assert.Equal(30, byActionText.TotalCount);

        var byIp = await service.SearchAsync(search: "10.0.0.59", pageSize: 100);
        Assert.Equal(1, byIp.TotalCount);
    }

    [Fact]
    public async Task GetStatsAsync_CountsTheWholeLogNotJustOnePage()
    {
        await using var db = CreateDb();
        for (var i = 0; i < 700; i++)
            db.AuditLogs.Add(new AuditLog { Action = AuditAction.LicenseRevoked, PerformedBy = "a" });
        db.AuditLogs.Add(new AuditLog { Action = AuditAction.LicenseSuspended, PerformedBy = "a" });
        db.AuditLogs.Add(new AuditLog
        {
            Action = AuditAction.CustomerCreated,
            PerformedBy = "a",
            Timestamp = DateTime.UtcNow.AddDays(-3)
        });
        await db.SaveChangesAsync();

        var stats = await new AuditLogService(db).GetStatsAsync();

        Assert.Equal(702, stats.TotalCount);
        Assert.Equal(701, stats.TodayCount);
        Assert.Equal(701, stats.SecurityFlagCount);
        Assert.Equal(700, stats.CriticalFlagCount);
    }

    [Fact]
    public async Task Dashboard_LicenseKpisMatchTheLicenseList()
    {
        // E1: licenses of suspended customers are excluded from both the KPI and the list.
        await using var db = CreateDb();
        var product = new ServiceProduct { Name = "Hostel", Code = "HOSTEL", IsAvailableForSale = true };
        var good = new Customer { Name = "Good", ContactEmail = "g@test.com" };
        var suspended = new Customer { Name = "Susp", ContactEmail = "s@test.com", IsSuspended = true };
        db.AddRange(product, good, suspended);
        License Make(Customer c, DateTime? expires) => new()
        {
            Customer = c,
            CustomerId = c.Id,
            ServiceProduct = product,
            ServiceProductId = product.Id,
            Status = LicenseStatus.Active,
            PlanName = "Growth",
            ExpiresAt = expires
        };
        db.Licenses.AddRange(
            Make(good, DateTime.UtcNow.AddDays(10)),
            Make(good, DateTime.UtcNow.AddDays(100)),
            Make(suspended, DateTime.UtcNow.AddDays(5)));
        await db.SaveChangesAsync();

        var stats = await new DashboardService(db).GetStatsAsync();

        Assert.Equal(2, stats.ActiveLicenses);
        Assert.Equal(1, stats.ExpiringWithin30Days);
        Assert.Equal(2, stats.CustomerCount);
    }

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options);
}
