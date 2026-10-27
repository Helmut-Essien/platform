using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Api.Services.Email;
using Platform.Shared.Dtos.Customers;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class CustomerServiceTests
{
    [Fact]
    public async Task SuspendAsync_PersistsChangeAndAuditTogetherAndDeniesAllLicenses()
    {
        // P11/P13
        await using var db = CreateDb();
        var denyList = new RecordingDenyList();
        var service = CreateService(db, denyList);
        var customer = new Customer { Name = "Acme", ContactEmail = "a@acme.test" };
        var product = new ServiceProduct { Name = "Hostel", Code = "HOSTEL" };
        db.AddRange(customer, product);
        for (var i = 0; i < 120; i++)
        {
            db.Licenses.Add(new License
            {
                CustomerId = customer.Id, Customer = customer,
                ServiceProductId = product.Id, ServiceProduct = product,
                PlanName = "Pro", Status = LicenseStatus.Active
            });
        }

        await db.SaveChangesAsync();

        var dto = await service.SuspendAsync(customer.Id, "admin@example.com");

        Assert.True(dto.IsSuspended);
        Assert.Equal(120, dto.LicenseCount);
        Assert.Equal(120, denyList.Denied.Count);
        Assert.True((await db.Customers.AsNoTracking().SingleAsync()).IsSuspended);
        Assert.Single(await db.AuditLogs.Where(a => a.Action == AuditAction.CustomerSuspended).ToListAsync());
        Assert.Contains(await db.EmailOutboxMessages.ToListAsync(), m => m.Kind == EmailDeliveryKind.Suspended);

        var back = await service.ReactivateAsync(customer.Id, "admin@example.com");
        Assert.False(back.IsSuspended);
        Assert.Equal(120, back.LicenseCount);
        Assert.Empty(denyList.Denied);
        Assert.Single(await db.AuditLogs.Where(a => a.Action == AuditAction.CustomerReactivated).ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_WritesCustomerAndAuditEntry()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new RecordingDenyList());

        var dto = await service.CreateAsync(
            new CreateCustomerRequest { Name = " Globex ", ContactEmail = "G@Globex.test" },
            "admin@example.com");

        Assert.Equal("Globex", dto.Name);
        Assert.Equal("g@globex.test", dto.ContactEmail);
        Assert.Single(await db.AuditLogs.Where(a => a.Action == AuditAction.CustomerCreated).ToListAsync());
    }

    private static CustomerService CreateService(AppDbContext db, ILicenseDenyListService denyList) =>
        new(db, new AuditLogService(db), denyList, new EmailOutboxService(db), new EmailTemplateService());

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options);

    private sealed class RecordingDenyList : ILicenseDenyListService
    {
        public HashSet<string> Denied { get; } = [];
        private readonly object _gate = new();

        public Task DenyLicenseAsync(string licenseId, CancellationToken cancellationToken = default)
        {
            lock (_gate) Denied.Add(licenseId);
            return Task.CompletedTask;
        }

        public Task DenyCustomerLicensesAsync(string customerId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> IsDeniedAsync(string licenseId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Denied.Contains(licenseId));

        public Task ClearLicenseDenyAsync(string licenseId, CancellationToken cancellationToken = default)
        {
            lock (_gate) Denied.Remove(licenseId);
            return Task.CompletedTask;
        }

        public Task ClearCustomerDenyAsync(string customerId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
