using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Api.Services.Email;
using Platform.Shared.Dtos.Audit;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Dtos.Common;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class LicenseServiceLifecycleTests
{
    [Fact]
    public async Task ActivateAsync_DeliversKeyAndSetsActive()
    {
        await using var db = CreateDbContext();
        var (customer, product, license) = await SeedPendingLicenseAsync(db);
        var auditLog = new FakeAuditLogService();
        var denyList = new FakeDenyListService();
        var service = CreateLicenseService(db, auditLog, denyList);

        var result = await service.ActivateAsync(
            license.Id,
            new ActivateLicenseRequest
            {
                EmailLicenseKey = true,
                CreateInvoice = false,
                SendInvoice = false,
                Subtotal = 0m,
                Currency = "USD"
            },
            performedBy: "admin@example.com");

        Assert.Equal(LicenseStatus.Active, result.Status);
        Assert.NotNull(result.LicenseKeySentAt);

        var stored = await db.Licenses.IgnoreQueryFilters().FirstAsync(l => l.Id == license.Id);
        Assert.Equal(LicenseStatus.Active, stored.Status);
        Assert.NotNull(stored.LicenseKeyHash);
        Assert.NotNull(stored.LicenseKeyLookupHash);
        Assert.Contains(auditLog.Entries, e => e.Action == AuditAction.LicenseActivated);
        Assert.Contains(db.EmailOutboxMessages, m => m.Kind == EmailDeliveryKind.LicenseKey);
        Assert.Empty(denyList.DeniedLicenseIds);
        _ = customer;
        _ = product;
    }

    [Fact]
    public async Task ActivateAsync_DoesNotSurfaceLicenseWhenCustomerSuspended()
    {
        await using var db = CreateDbContext();
        var (_, _, license) = await SeedPendingLicenseAsync(db, customerSuspended: true);
        var service = CreateLicenseService(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ActivateAsync(
                license.Id,
                new ActivateLicenseRequest
                {
                    EmailLicenseKey = true,
                    CreateInvoice = false,
                    Currency = "USD"
                },
                performedBy: "admin@example.com"));

        // Global query filter hides licenses for suspended customers.
        Assert.Equal("License not found.", ex.Message);
    }

    [Fact]
    public async Task RevokeAsync_SetsRevokedDeniesAndQueuesEmail()
    {
        await using var db = CreateDbContext();
        var (_, _, license) = await SeedPendingLicenseAsync(db);
        var auditLog = new FakeAuditLogService();
        var denyList = new FakeDenyListService();
        var service = CreateLicenseService(db, auditLog, denyList);

        await service.ActivateAsync(
            license.Id,
            new ActivateLicenseRequest
            {
                EmailLicenseKey = true,
                CreateInvoice = false,
                Currency = "USD"
            },
            performedBy: "admin@example.com");

        var result = await service.RevokeAsync(license.Id, performedBy: "admin@example.com");

        Assert.Equal(LicenseStatus.Revoked, result.Status);
        Assert.Contains(license.Id, denyList.DeniedLicenseIds);
        Assert.Contains(auditLog.Entries, e => e.Action == AuditAction.LicenseRevoked);
        Assert.Contains(db.EmailOutboxMessages, m => m.Kind == EmailDeliveryKind.Revoked);
    }

    [Fact]
    public async Task RevokeAsync_RejectsAlreadyRevoked()
    {
        await using var db = CreateDbContext();
        var (_, _, license) = await SeedPendingLicenseAsync(db);
        var service = CreateLicenseService(db);

        await service.ActivateAsync(
            license.Id,
            new ActivateLicenseRequest
            {
                EmailLicenseKey = true,
                CreateInvoice = false,
                Currency = "USD"
            },
            performedBy: "admin@example.com");
        await service.RevokeAsync(license.Id, performedBy: "admin@example.com");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RevokeAsync(license.Id, performedBy: "admin@example.com"));

        Assert.Equal("License is already revoked.", ex.Message);
    }

    private static LicenseService CreateLicenseService(
        AppDbContext db,
        IAuditLogService? auditLog = null,
        ILicenseDenyListService? denyList = null)
    {
        auditLog ??= new FakeAuditLogService();
        denyList ??= new FakeDenyListService();
        var outbox = new EmailOutboxService(db);
        var templates = new EmailTemplateService();
        var protector = CreateProtector();
        var keyDelivery = new LicenseKeyDeliveryService(db, outbox, protector, templates);
        var billing = new StubBillingService();

        return new LicenseService(db, billing, auditLog, keyDelivery, denyList, outbox, templates);
    }

    private static EmailPayloadProtector CreateProtector()
    {
        var settings = Options.Create(new EmailSettings
        {
            Outbox = new EmailOutboxSettings
            {
                EncryptionKey = Convert.ToBase64String(new byte[32])
            }
        });
        return new EmailPayloadProtector(
            settings,
            new ConfigurationBuilder().Build(),
            new FakeHostEnvironment { EnvironmentName = Environments.Development });
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<(Customer Customer, ServiceProduct Product, License License)> SeedPendingLicenseAsync(
        AppDbContext db,
        bool customerSuspended = false)
    {
        var customer = new Customer
        {
            Name = "Acme Ltd",
            ContactEmail = "owner@acme.test",
            IsSuspended = customerSuspended
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
            Status = LicenseStatus.Pending,
            PlanName = "Growth"
        };

        db.AddRange(customer, product, license);
        await db.SaveChangesAsync();
        return (customer, product, license);
    }

    private sealed class StubBillingService : IBillingService
    {
        public Task<InvoiceDto> CreateInvoiceAsync(
            CreateInvoiceRequest request,
            string performedBy,
            string? ipAddress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InvoiceDto> CreateInvoiceForLicenseAsync(
            License license,
            decimal subtotal,
            decimal taxAmount,
            string currency,
            DateTime? dueDate,
            string? description,
            string performedBy,
            InvoiceStatus status = InvoiceStatus.Sent,
            string? ipAddress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InvoiceDto?> GetInvoiceAsync(string id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InvoiceDto> SendInvoiceAsync(
            string id,
            string performedBy,
            string? ipAddress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PagedResult<InvoiceDto>> ListInvoicesAsync(
            string? customerId = null,
            int page = 1,
            int pageSize = 25,
            bool unpaidOnly = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InvoiceDto> VoidInvoiceAsync(
            string id,
            string performedBy,
            string? ipAddress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReceiptDto> RecordReceiptAsync(
            string invoiceId,
            RecordReceiptRequest request,
            string performedBy,
            string? ipAddress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReceiptDto> ReverseReceiptAsync(
            string invoiceId,
            string receiptId,
            ReverseReceiptRequest request,
            string performedBy,
            string? ipAddress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDenyListService : ILicenseDenyListService
    {
        public List<string> DeniedLicenseIds { get; } = [];

        public Task DenyLicenseAsync(string licenseId, CancellationToken cancellationToken = default)
        {
            DeniedLicenseIds.Add(licenseId);
            return Task.CompletedTask;
        }

        public Task DenyCustomerLicensesAsync(string customerId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> IsDeniedAsync(string licenseId, CancellationToken cancellationToken = default) =>
            Task.FromResult(DeniedLicenseIds.Contains(licenseId));

        public Task ClearLicenseDenyAsync(string licenseId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearCustomerDenyAsync(string customerId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeAuditLogService : IAuditLogService
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task<IReadOnlyList<AuditLogDto>> ListAsync(
            string? customerId = null,
            string? licenseId = null,
            AuditAction? action = null,
            int limit = 100,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditLogDto>>([]);

        public Task WriteAsync(
            AuditAction action,
            string performedBy,
            string? customerId = null,
            string? licenseId = null,
            string? invoiceId = null,
            string? detailsJson = null,
            string? ipAddress = null,
            CancellationToken cancellationToken = default)
        {
            Entries.Add(new AuditEntry(action, performedBy, customerId, licenseId, invoiceId));
            return Task.CompletedTask;
        }
    }

    private sealed record AuditEntry(
        AuditAction Action,
        string PerformedBy,
        string? CustomerId,
        string? LicenseId,
        string? InvoiceId);

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "API.Tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
