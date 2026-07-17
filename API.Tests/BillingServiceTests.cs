using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Api.Services.Billing;
using Platform.Api.Services.Email;
using Platform.Shared.Dtos.Audit;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class BillingServiceTests
{
    [Fact]
    public async Task CreateInvoiceAsync_CreatesSentInvoiceAndAuditLog()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var auditLog = new FakeAuditLogService();
        var service = CreateBillingService(db, auditLog);

        var invoice = await service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            Status = InvoiceStatus.Sent,
            Currency = "usd",
            Subtotal = 100m,
            TaxAmount = 12.5m,
            PlanName = "Growth",
            Description = "Monthly license"
        }, performedBy: "admin@example.com");

        Assert.Equal("INV-", invoice.InvoiceNumber[..4]);
        Assert.Equal(InvoiceStatus.Sent, invoice.Status);
        Assert.Equal("USD", invoice.Currency);
        Assert.Equal(112.5m, invoice.TotalAmount);
        Assert.Equal(0m, invoice.AmountPaid);
        Assert.Equal(112.5m, invoice.AmountDue);
        Assert.Contains(auditLog.Entries, e =>
            e.Action == AuditAction.InvoiceSent &&
            e.CustomerId == customer.Id &&
            e.InvoiceId == invoice.Id);
    }

    [Fact]
    public async Task CreateInvoiceAsync_QueuesEmailWhenStatusIsSent()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);

        await service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            Status = InvoiceStatus.Sent,
            Currency = "USD",
            Subtotal = 100m,
            TaxAmount = 20m,
            PlanName = "Pro",
            Description = "June 2026 license"
        }, performedBy: "admin@example.com");

        var message = Assert.Single(db.EmailOutboxMessages);
        Assert.Equal("billing@acme.test", message.ToEmail);
        Assert.Contains("Invoice", message.Subject);
        Assert.Equal(EmailDeliveryKind.Invoice, message.Kind);
    }

    [Fact]
    public async Task CreateInvoiceAsync_DoesNotSendEmailForDraftInvoice()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);

        await service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            Status = InvoiceStatus.Draft,
            Currency = "USD",
            Subtotal = 50m,
            TaxAmount = 0m
        }, performedBy: "admin@example.com");

        Assert.Empty(db.EmailOutboxMessages);
    }

    [Fact]
    public async Task CreateInvoiceAsync_RejectsSuspendedCustomer()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db, isSuspended: true);
        var service = CreateBillingService(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                CustomerId = customer.Id,
                Status = InvoiceStatus.Sent,
                Currency = "USD",
                Subtotal = 100m,
                TaxAmount = 0m
            }, performedBy: "admin@example.com"));

        Assert.Equal("Cannot create invoice for a suspended customer.", ex.Message);
    }

    [Fact]
    public async Task RecordReceiptAsync_PartialPaymentUpdatesInvoiceAndWritesAuditLog()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var auditLog = new FakeAuditLogService();
        var service = CreateBillingService(db, auditLog);
        var invoice = await CreateSentInvoiceAsync(service, customer.Id, 100m);

        var receipt = await service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "pay-1",
            AmountPaid = 40m,
            PaymentMethod = PaymentMethod.BankTransfer,
            PaymentReference = "BANK-123"
        }, performedBy: "admin@example.com");

        var updatedInvoice = await service.GetInvoiceAsync(invoice.Id);

        Assert.Equal("RCP-", receipt.ReceiptNumber[..4]);
        Assert.Equal(ReceiptStatus.Posted, receipt.Status);
        Assert.Equal(40m, receipt.AmountPaid);
        Assert.Equal(InvoiceStatus.PartiallyPaid, updatedInvoice!.Status);
        Assert.Equal(40m, updatedInvoice.AmountPaid);
        Assert.Equal(60m, updatedInvoice.AmountDue);
        Assert.Single(updatedInvoice.Transactions);
        Assert.Contains(auditLog.Entries, e =>
            e.Action == AuditAction.ReceiptRecorded &&
            e.InvoiceId == invoice.Id);
    }

    [Fact]
    public async Task RecordReceiptAsync_FullPaymentMarksInvoicePaid()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);
        var invoice = await CreateSentInvoiceAsync(service, customer.Id, 100m, tax: 10m);

        await service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "pay-full",
            AmountPaid = 110m,
            PaymentMethod = PaymentMethod.Cash
        }, performedBy: "admin@example.com");

        var updatedInvoice = await service.GetInvoiceAsync(invoice.Id);

        Assert.Equal(InvoiceStatus.Paid, updatedInvoice!.Status);
        Assert.Equal(110m, updatedInvoice.AmountPaid);
        Assert.Equal(0m, updatedInvoice.AmountDue);
    }

    [Fact]
    public async Task RecordReceiptAsync_RejectsOverpayment()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);
        var invoice = await CreateSentInvoiceAsync(service, customer.Id, 100m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
            {
                IdempotencyKey = "overpay",
                AmountPaid = 101m,
                PaymentMethod = PaymentMethod.Cash
            }, performedBy: "admin@example.com"));

        Assert.Contains("exceeds balance due", ex.Message);
    }

    [Fact]
    public async Task RecordReceiptAsync_RejectsDraftInvoice()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);
        var invoice = await service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            Status = InvoiceStatus.Draft,
            Currency = "USD",
            Subtotal = 50m,
            TaxAmount = 0m
        }, performedBy: "admin@example.com");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
            {
                IdempotencyKey = "draft-pay",
                AmountPaid = 50m,
                PaymentMethod = PaymentMethod.Cash
            }, performedBy: "admin@example.com"));

        Assert.Contains("Draft", ex.Message);
    }

    [Fact]
    public async Task RecordReceiptAsync_RequiresReferenceForBankTransfer()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);
        var invoice = await CreateSentInvoiceAsync(service, customer.Id, 50m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
            {
                IdempotencyKey = "no-ref",
                AmountPaid = 50m,
                PaymentMethod = PaymentMethod.BankTransfer
            }, performedBy: "admin@example.com"));

        Assert.Contains("reference", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordReceiptAsync_IdempotentReplayReturnsSameReceipt()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);
        var invoice = await CreateSentInvoiceAsync(service, customer.Id, 100m);

        var first = await service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "same-key",
            AmountPaid = 40m,
            PaymentMethod = PaymentMethod.BankTransfer,
            PaymentReference = "REF-A"
        }, performedBy: "admin@example.com");

        var second = await service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "same-key",
            AmountPaid = 40m,
            PaymentMethod = PaymentMethod.BankTransfer,
            PaymentReference = "REF-A"
        }, performedBy: "admin@example.com");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await db.Receipts.CountAsync());
        Assert.Equal(1, await db.PaymentTransactions.CountAsync());
        var updated = await service.GetInvoiceAsync(invoice.Id);
        Assert.Equal(40m, updated!.AmountPaid);
    }

    [Fact]
    public async Task RecordReceiptAsync_RejectsDuplicateMethodAndReference()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);
        var invoice = await CreateSentInvoiceAsync(service, customer.Id, 100m);

        await service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "k1",
            AmountPaid = 20m,
            PaymentMethod = PaymentMethod.MobileMoney,
            PaymentReference = "MM-1"
        }, performedBy: "admin@example.com");

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
            {
                IdempotencyKey = "k2",
                AmountPaid = 20m,
                PaymentMethod = PaymentMethod.MobileMoney,
                PaymentReference = "MM-1"
            }, performedBy: "admin@example.com"));
    }

    [Fact]
    public async Task RecordReceiptAsync_QueuesPaymentReceiptEmailWhenCustomerNotTracked()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var createService = CreateBillingService(db);
        var invoice = await CreateSentInvoiceAsync(createService, customer.Id, 100m);

        db.ChangeTracker.Clear();
        var recordService = CreateBillingService(db);

        await recordService.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "email-key",
            AmountPaid = 100m,
            PaymentMethod = PaymentMethod.BankTransfer,
            PaymentReference = "REF-1"
        }, performedBy: "admin@example.com");

        var message = Assert.Single(db.EmailOutboxMessages.Where(m => m.Kind == EmailDeliveryKind.PaymentReceipt));
        Assert.Equal("billing@acme.test", message.ToEmail);
        Assert.Contains("Receipt", message.Subject);
        Assert.Equal(invoice.Id, message.InvoiceId);
    }

    [Fact]
    public async Task ReverseReceiptAsync_RestoresBalanceAndAllowsVoid()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var auditLog = new FakeAuditLogService();
        var service = CreateBillingService(db, auditLog);
        var invoice = await CreateSentInvoiceAsync(service, customer.Id, 100m);

        var receipt = await service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "to-reverse",
            AmountPaid = 100m,
            PaymentMethod = PaymentMethod.Cash
        }, performedBy: "admin@example.com");

        var reversed = await service.ReverseReceiptAsync(invoice.Id, receipt.Id, new ReverseReceiptRequest
        {
            IdempotencyKey = "rev-1",
            Reason = "Entered against wrong invoice"
        }, performedBy: "admin@example.com");

        Assert.Equal(ReceiptStatus.Reversed, reversed.Status);
        Assert.Equal("Entered against wrong invoice", reversed.ReversalReason);

        var updated = await service.GetInvoiceAsync(invoice.Id);
        Assert.Equal(0m, updated!.AmountPaid);
        Assert.Equal(100m, updated.AmountDue);
        Assert.Equal(InvoiceStatus.Sent, updated.Status);
        Assert.Equal(2, updated.Transactions.Count);
        Assert.Contains(auditLog.Entries, e => e.Action == AuditAction.ReceiptReversed);

        var voided = await service.VoidInvoiceAsync(invoice.Id, "admin@example.com");
        Assert.Equal(InvoiceStatus.Void, voided.Status);
    }

    [Fact]
    public async Task ReverseReceiptAsync_RejectsSecondReverse()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);
        var invoice = await CreateSentInvoiceAsync(service, customer.Id, 50m);
        var receipt = await service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "once",
            AmountPaid = 50m,
            PaymentMethod = PaymentMethod.Cash
        }, performedBy: "admin@example.com");

        await service.ReverseReceiptAsync(invoice.Id, receipt.Id, new ReverseReceiptRequest
        {
            IdempotencyKey = "rev-a",
            Reason = "mistake"
        }, performedBy: "admin@example.com");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReverseReceiptAsync(invoice.Id, receipt.Id, new ReverseReceiptRequest
            {
                IdempotencyKey = "rev-b",
                Reason = "again"
            }, performedBy: "admin@example.com"));
    }

    [Fact]
    public async Task RecordReceiptAsync_ReactivatesLicenseAutoSuspendedForOverdue()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var product = await SeedProductAsync(db);
        var license = new License
        {
            CustomerId = customer.Id,
            ServiceProductId = product.Id,
            PlanName = "Pro",
            Status = LicenseStatus.Suspended,
            AutoSuspendedForOverdueInvoiceId = "pending"
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();

        var denyList = new FakeDenyListService();
        var auditLog = new FakeAuditLogService();
        var service = CreateBillingService(db, auditLog, denyList);
        var invoice = await service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            LicenseId = license.Id,
            ServiceProductId = product.Id,
            Status = InvoiceStatus.Overdue,
            Currency = "USD",
            Subtotal = 80m,
            TaxAmount = 0m,
            DueDate = DateTime.UtcNow.AddDays(-1)
        }, performedBy: "admin@example.com");

        // CreateInvoice forces Sent when SendImmediately; set overdue for this scenario.
        var stored = await db.Invoices.IgnoreQueryFilters().FirstAsync(i => i.Id == invoice.Id);
        stored.Status = InvoiceStatus.Overdue;
        stored.DueDate = DateTime.UtcNow.AddDays(-2);
        license.AutoSuspendedForOverdueInvoiceId = invoice.Id;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "reactivate",
            AmountPaid = 80m,
            PaymentMethod = PaymentMethod.Cash
        }, performedBy: "admin@example.com");

        var reloaded = await db.Licenses.IgnoreQueryFilters().FirstAsync(l => l.Id == license.Id);
        Assert.Equal(LicenseStatus.Active, reloaded.Status);
        Assert.Null(reloaded.AutoSuspendedForOverdueInvoiceId);
        Assert.Contains(license.Id, denyList.ClearedLicenseIds);
        Assert.Contains(auditLog.Entries, e => e.Action == AuditAction.LicenseAutoReactivatedPaid);
        Assert.Contains(db.EmailOutboxMessages, m => m.Kind == EmailDeliveryKind.LicenseReactivated);
    }

    [Fact]
    public async Task RecordReceiptAsync_DoesNotReactivateManuallySuspendedLicense()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var product = await SeedProductAsync(db);
        var license = new License
        {
            CustomerId = customer.Id,
            ServiceProductId = product.Id,
            PlanName = "Pro",
            Status = LicenseStatus.Suspended,
            AutoSuspendedForOverdueInvoiceId = null
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();

        var denyList = new FakeDenyListService();
        var service = CreateBillingService(db, denyList: denyList);
        var invoice = await service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            LicenseId = license.Id,
            ServiceProductId = product.Id,
            Status = InvoiceStatus.Sent,
            Currency = "USD",
            Subtotal = 80m,
            TaxAmount = 0m
        }, performedBy: "admin@example.com");

        await service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "manual-suspend",
            AmountPaid = 80m,
            PaymentMethod = PaymentMethod.Cash
        }, performedBy: "admin@example.com");

        var reloaded = await db.Licenses.IgnoreQueryFilters().FirstAsync(l => l.Id == license.Id);
        Assert.Equal(LicenseStatus.Suspended, reloaded.Status);
        Assert.Empty(denyList.ClearedLicenseIds);
    }

    private static async Task<InvoiceDto> CreateSentInvoiceAsync(
        BillingService service,
        string customerId,
        decimal subtotal,
        decimal tax = 0m)
    {
        return await service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customerId,
            Status = InvoiceStatus.Sent,
            Currency = "USD",
            Subtotal = subtotal,
            TaxAmount = tax
        }, performedBy: "admin@example.com");
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    private static BillingService CreateBillingService(
        AppDbContext db,
        IAuditLogService? auditLog = null,
        ILicenseDenyListService? denyList = null)
    {
        return new BillingService(
            db,
            auditLog ?? new FakeAuditLogService(),
            new EmailOutboxService(db),
            new EmailTemplateService(),
            denyList ?? new FakeDenyListService());
    }

    private static async Task<Customer> SeedCustomerAsync(AppDbContext db, bool isSuspended = false)
    {
        var customer = new Customer
        {
            Name = "Acme Ltd",
            ContactEmail = "billing@acme.test",
            IsSuspended = isSuspended
        };

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

    private sealed class FakeDenyListService : ILicenseDenyListService
    {
        public List<string> ClearedLicenseIds { get; } = [];

        public Task DenyLicenseAsync(string licenseId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DenyCustomerLicensesAsync(string customerId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> IsDeniedAsync(string licenseId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task ClearLicenseDenyAsync(string licenseId, CancellationToken cancellationToken = default)
        {
            ClearedLicenseIds.Add(licenseId);
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
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AuditLogDto>>([]);
        }

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
            Entries.Add(new AuditEntry(action, performedBy, customerId, licenseId, invoiceId, detailsJson, ipAddress));
            return Task.CompletedTask;
        }
    }

    private sealed record AuditEntry(
        AuditAction Action,
        string PerformedBy,
        string? CustomerId,
        string? LicenseId,
        string? InvoiceId,
        string? DetailsJson,
        string? IpAddress);
}
