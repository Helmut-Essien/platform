using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Api.Services.Email;
using Platform.Shared.Dtos.Audit;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class OverdueInvoiceLifecycleWorkerTests
{
    [Fact]
    public async Task ProcessAsync_MarksInvoiceOverdueWithoutSuspendingWhenAutoSuspendDisabled()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var product = await SeedProductAsync(db);
        var license = await SeedLicenseAsync(db, customer.Id, product.Id, LicenseStatus.Active);
        var invoice = new Invoice
        {
            CustomerId = customer.Id,
            LicenseId = license.Id,
            ServiceProductId = product.Id,
            InvoiceNumber = "INV-2026-00001",
            Status = InvoiceStatus.Sent,
            Currency = "USD",
            Subtotal = 100m,
            TaxAmount = 0m,
            TotalAmount = 100m,
            DueDate = DateTime.UtcNow.AddDays(-1)
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var licenses = new TrackingLicenseService();
        var worker = CreateWorker(db, licenses, autoSuspend: false);
        await worker.ProcessAsync(CancellationToken.None);

        var updated = await db.Invoices.IgnoreQueryFilters().FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(InvoiceStatus.Overdue, updated.Status);
        Assert.Empty(licenses.SuspendedIds);
    }

    [Fact]
    public async Task ProcessAsync_SuspendsActiveLicenseWhenAutoSuspendEnabled()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var product = await SeedProductAsync(db);
        var license = await SeedLicenseAsync(db, customer.Id, product.Id, LicenseStatus.Active);
        var invoice = new Invoice
        {
            CustomerId = customer.Id,
            LicenseId = license.Id,
            ServiceProductId = product.Id,
            InvoiceNumber = "INV-2026-00002",
            Status = InvoiceStatus.Sent,
            Currency = "USD",
            Subtotal = 100m,
            TaxAmount = 0m,
            TotalAmount = 100m,
            DueDate = DateTime.UtcNow.AddDays(-3)
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var licenses = new TrackingLicenseService();
        var worker = CreateWorker(db, licenses, autoSuspend: true);
        await worker.ProcessAsync(CancellationToken.None);

        Assert.Contains(license.Id, licenses.SuspendedIds);
        Assert.Equal(invoice.Id, licenses.AutoSuspendedInvoiceIds[license.Id]);
    }

    private static OverdueInvoiceLifecycleWorker CreateWorker(
        AppDbContext db,
        ILicenseService licenseService,
        bool autoSuspend)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(licenseService);
        services.AddSingleton<IAuditLogService, NoopAuditLogService>();
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        return new OverdueInvoiceLifecycleWorker(
            scopeFactory,
            Options.Create(new LifecycleSettings
            {
                AutoSuspendOnOverdue = autoSuspend,
                OverduePollMinutes = 5
            }),
            NullLogger<OverdueInvoiceLifecycleWorker>.Instance);
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<Customer> SeedCustomerAsync(AppDbContext db)
    {
        var customer = new Customer { Name = "Acme", ContactEmail = "a@test.com" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<ServiceProduct> SeedProductAsync(AppDbContext db)
    {
        var product = new ServiceProduct
        {
            Name = "Hostel",
            Code = "HOSTEL",
            Description = "Hostel",
            IsAvailableForSale = true
        };
        db.ServiceProducts.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    private static async Task<License> SeedLicenseAsync(
        AppDbContext db,
        string customerId,
        string productId,
        LicenseStatus status)
    {
        var license = new License
        {
            CustomerId = customerId,
            ServiceProductId = productId,
            PlanName = "Pro",
            Status = status
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license;
    }

    private sealed class TrackingLicenseService : ILicenseService
    {
        public List<string> SuspendedIds { get; } = [];
        public Dictionary<string, string?> AutoSuspendedInvoiceIds { get; } = new();

        public Task<LicenseDto> SuspendAsync(
            string id,
            string performedBy,
            string? ipAddress = null,
            CancellationToken cancellationToken = default,
            string? notificationReason = null,
            string? autoSuspendedForOverdueInvoiceId = null)
        {
            SuspendedIds.Add(id);
            AutoSuspendedInvoiceIds[id] = autoSuspendedForOverdueInvoiceId;
            return Task.FromResult(new LicenseDto
            {
                Id = id,
                CustomerId = "c",
                ServiceProductId = "s",
                ServiceProductCode = "HOSTEL",
                Status = LicenseStatus.Suspended,
                PlanName = "Pro",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        public Task<LicenseDto> CreateAsync(CreateLicenseRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<Platform.Shared.Dtos.Common.PagedResult<LicenseDto>> ListAsync(string? customerId = null, bool includeSuspendedCustomers = false, int page = 1, int pageSize = 25, int? expiringWithinDays = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LicenseDto?> GetAsync(string id, bool includeSuspendedCustomers = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LicenseDto> UpdateAsync(string id, UpdateLicenseRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LicenseDto> RevokeAsync(string id, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LicenseDto> ActivateAsync(string id, ActivateLicenseRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LicenseDto> RenewAsync(string id, RenewLicenseRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LicenseDto> ResendKeyAsync(string id, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LicenseDto> RotateKeyAsync(string id, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopAuditLogService : IAuditLogService
    {
        public Task<IReadOnlyList<AuditLogDto>> ListAsync(string? customerId = null, string? licenseId = null, AuditAction? action = null, int limit = 100, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditLogDto>>([]);

        public Task WriteAsync(AuditAction action, string performedBy, string? customerId = null, string? licenseId = null, string? invoiceId = null, string? detailsJson = null, string? ipAddress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
