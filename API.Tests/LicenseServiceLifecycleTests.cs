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
        Assert.Null(result.LicenseKeySentAt);

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

        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
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

    [Fact]
    public async Task ActivateAsync_ClearsDenyListAfterSuspend()
    {
        await using var db = CreateDbContext();
        var (_, _, license) = await SeedPendingLicenseAsync(db);
        var denyList = new FakeDenyListService();
        var service = CreateLicenseService(db, denyList: denyList);
        var activate = new ActivateLicenseRequest
        {
            EmailLicenseKey = true,
            CreateInvoice = false,
            Currency = "USD"
        };

        await service.ActivateAsync(license.Id, activate, performedBy: "admin@example.com");
        await service.SuspendAsync(license.Id, performedBy: "admin@example.com");
        Assert.Contains(license.Id, denyList.DeniedLicenseIds);

        await service.ActivateAsync(license.Id, activate, performedBy: "admin@example.com");
        Assert.DoesNotContain(license.Id, denyList.DeniedLicenseIds);
    }

    private static async Task<(AppDbContext Db, LicenseService Service, FakeDenyListService Deny, License License, string OriginalHash)>
        SeedSuspendedLicenseAsync()
    {
        var db = CreateDbContext();
        var (_, _, license) = await SeedPendingLicenseAsync(db);
        var deny = new FakeDenyListService();
        var service = CreateLicenseService(db, denyList: deny);
        await service.ActivateAsync(
            license.Id,
            new ActivateLicenseRequest { EmailLicenseKey = true, CreateInvoice = false, Currency = "USD" },
            performedBy: "admin@example.com");
        await service.SuspendAsync(license.Id, performedBy: "admin@example.com");
        var stored = await db.Licenses.IgnoreQueryFilters().AsNoTracking().FirstAsync(l => l.Id == license.Id);
        return (db, service, deny, license, stored.LicenseKeyHash!);
    }

    [Fact]
    public async Task ListAsync_FiltersByStatusProductAndSearchOnTheServer()
    {
        // B2: filters are applied before paging, so totals and pages are correct.
        var (db, service, _, license, _) = await SeedSuspendedLicenseAsync();
        await using var _ = db;
        var stored = await db.Licenses.IgnoreQueryFilters().Include(l => l.Customer).FirstAsync(l => l.Id == license.Id);

        var suspended = await service.ListAsync(status: LicenseStatus.Suspended);
        Assert.Equal(license.Id, Assert.Single(suspended.Items).Id);
        Assert.Equal(1, suspended.TotalCount);

        Assert.Empty((await service.ListAsync(status: LicenseStatus.Active)).Items);
        Assert.Single((await service.ListAsync(serviceProductId: stored.ServiceProductId)).Items);
        Assert.Empty((await service.ListAsync(serviceProductId: "missing")).Items);
        Assert.Single((await service.ListAsync(search: stored.Customer.Name.ToUpperInvariant())).Items);
        Assert.Single((await service.ListAsync(search: stored.PlanName.ToLowerInvariant())).Items);
        Assert.Empty((await service.ListAsync(search: "no-such-text")).Items);
    }

    [Fact]
    public async Task ActivateAsync_OnSuspendedLicense_WithDefaultFlags_DoesNotRotateKeyOrInvoice()
    {
        // B4: default request flags (email key + create invoice) must not apply when restoring.
        var (db, service, deny, license, originalHash) = await SeedSuspendedLicenseAsync();
        await using var _ = db;
        var keyEmailsBefore = db.EmailOutboxMessages.Count(m => m.Kind == EmailDeliveryKind.LicenseKey);

        // StubBillingService throws if an invoice is attempted.
        var result = await service.ActivateAsync(license.Id, new ActivateLicenseRequest(), performedBy: "admin@example.com");

        Assert.Equal(LicenseStatus.Active, result.Status);
        var stored = await db.Licenses.IgnoreQueryFilters().AsNoTracking().FirstAsync(l => l.Id == license.Id);
        Assert.Equal(originalHash, stored.LicenseKeyHash);
        Assert.Equal(keyEmailsBefore, db.EmailOutboxMessages.Count(m => m.Kind == EmailDeliveryKind.LicenseKey));
        Assert.DoesNotContain(license.Id, deny.DeniedLicenseIds);
    }

    [Fact]
    public async Task ReactivateAsync_RestoresSuspendedLicenseWithoutKeyOrBilling()
    {
        var (db, service, deny, license, originalHash) = await SeedSuspendedLicenseAsync();
        await using var _ = db;
        var outboxBefore = db.EmailOutboxMessages.Count();

        var result = await service.ReactivateAsync(license.Id, performedBy: "admin@example.com");

        Assert.Equal(LicenseStatus.Active, result.Status);
        var stored = await db.Licenses.IgnoreQueryFilters().AsNoTracking().FirstAsync(l => l.Id == license.Id);
        Assert.Equal(originalHash, stored.LicenseKeyHash);
        Assert.Null(stored.AutoSuspendedForOverdueInvoiceId);
        Assert.Equal(outboxBefore, db.EmailOutboxMessages.Count());
        Assert.DoesNotContain(license.Id, deny.DeniedLicenseIds);
    }

    [Fact]
    public async Task ReactivateAsync_RejectsNonSuspendedLicense()
    {
        await using var db = CreateDbContext();
        var (_, _, license) = await SeedPendingLicenseAsync(db);
        var service = CreateLicenseService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReactivateAsync(license.Id, performedBy: "admin@example.com"));
    }

    [Fact]
    public async Task RenewAsync_OnSuspendedLicense_ExtendsExpiryButKeepsItSuspended()
    {
        // B8: renewal must not silently lift a suspension.
        var (db, service, deny, license, _) = await SeedSuspendedLicenseAsync();
        await using var _ = db;
        var newExpiry = DateTime.UtcNow.AddDays(90);

        var result = await service.RenewAsync(
            license.Id,
            new RenewLicenseRequest { ExpiresAt = newExpiry, CreateInvoice = false, EmailLicenseKey = true },
            performedBy: "admin@example.com");

        Assert.Equal(LicenseStatus.Suspended, result.Status);
        Assert.Contains(license.Id, deny.DeniedLicenseIds);
        var stored = await db.Licenses.IgnoreQueryFilters().AsNoTracking().FirstAsync(l => l.Id == license.Id);
        Assert.Equal(LicenseStatus.Suspended, stored.Status);
        Assert.NotNull(stored.ExpiresAt);
        Assert.DoesNotContain(db.EmailOutboxMessages, m => m.Kind == EmailDeliveryKind.RenewalConfirmation);
    }

    [Fact]
    public async Task RenewAsync_OnExpiredLicense_ReactivatesIt()
    {
        await using var db = CreateDbContext();
        var (_, _, license) = await SeedPendingLicenseAsync(db);
        var service = CreateLicenseService(db);
        await service.ActivateAsync(
            license.Id,
            new ActivateLicenseRequest { EmailLicenseKey = true, CreateInvoice = false, Currency = "USD" },
            performedBy: "admin@example.com");
        var tracked = await db.Licenses.IgnoreQueryFilters().FirstAsync(l => l.Id == license.Id);
        tracked.Status = LicenseStatus.Expired;
        await db.SaveChangesAsync();

        var result = await service.RenewAsync(
            license.Id,
            new RenewLicenseRequest { ExpiresAt = DateTime.UtcNow.AddDays(30), CreateInvoice = false },
            performedBy: "admin@example.com");

        Assert.Equal(LicenseStatus.Active, result.Status);
    }

    [Fact]
    public async Task ExpireDueLicensesAsync_MarksPastExpiryAndDenies()
    {
        await using var db = CreateDbContext();
        var (_, _, license) = await SeedPendingLicenseAsync(db);
        license.Status = LicenseStatus.Active;
        license.ExpiresAt = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();

        var denyList = new FakeDenyListService();
        var service = CreateLicenseService(db, denyList: denyList);
        var count = await service.ExpireDueLicensesAsync();

        Assert.Equal(1, count);
        var stored = await db.Licenses.IgnoreQueryFilters().FirstAsync(l => l.Id == license.Id);
        Assert.Equal(LicenseStatus.Expired, stored.Status);
        Assert.Contains(license.Id, denyList.DeniedLicenseIds);
    }

    [Fact]
    public async Task CreateAsync_RejectsSecondOpenLicenseForSameService()
    {
        await using var db = CreateDbContext();
        var (customer, product, _) = await SeedPendingLicenseAsync(db);
        var service = CreateLicenseService(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(new CreateLicenseRequest
            {
                CustomerId = customer.Id,
                ServiceProductId = product.Id,
                PlanName = "Second"
            }, performedBy: "admin@example.com"));

        Assert.Contains("already has a license", ex.Message);
    }

    [Fact]
    public async Task UpdateAsync_ConflictingWritersThrowConcurrencyException()
    {
        var databaseName = NUlid.Ulid.NewUlid().ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        string licenseId;
        await using (var seed = new AppDbContext(options))
        {
            var (_, _, license) = await SeedPendingLicenseAsync(seed);
            licenseId = license.Id;
        }

        await using var first = new AppDbContext(options);
        await using var second = new AppDbContext(options);
        var left = await first.Licenses.IgnoreQueryFilters().FirstAsync(l => l.Id == licenseId);
        var right = await second.Licenses.IgnoreQueryFilters().FirstAsync(l => l.Id == licenseId);
        left.PlanName = "Left";
        right.PlanName = "Right";
        await first.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
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
        public Task<InvoiceDto?> CreateRenewalInvoiceAsync(string licenseId, DateTime? expectedExpiresAt, string performedBy,
            bool explicitRequest = false, string? ipAddress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> GetPayLinkAsync(string invoiceId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
            InvoiceStatus? status = null,
            string? search = null,
            DateTime? issuedFrom = null,
            DateTime? issuedTo = null,
            string? sortBy = null,
            bool sortDesc = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InvoiceStatsDto> GetInvoiceStatsAsync(CancellationToken cancellationToken = default) =>
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

        public Task ClearLicenseDenyAsync(string licenseId, CancellationToken cancellationToken = default)
        {
            DeniedLicenseIds.Remove(licenseId);
            return Task.CompletedTask;
        }

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
