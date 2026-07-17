using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
        var emailSender = new CapturingEmailSender();
        var service = CreateBillingService(db, auditLog, emailSender);

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
        var emailSender = new CapturingEmailSender();
        var service = CreateBillingService(db, emailSender: emailSender);

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
        Assert.Contains("$100.00", message.HtmlBody);
        Assert.Contains("$20.00", message.HtmlBody);
        Assert.Contains("$120.00", message.HtmlBody);
        Assert.Contains("Pro", message.HtmlBody);
        Assert.Contains("June 2026 license", message.HtmlBody);
        Assert.Contains("attached", message.HtmlBody);
        Assert.Equal(EmailDeliveryKind.Invoice, message.Kind);
        Assert.NotNull(message.InvoiceId);
    }

    [Fact]
    public async Task CreateInvoiceAsync_DoesNotSendEmailForDraftInvoice()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var emailSender = new CapturingEmailSender();
        var service = CreateBillingService(db, emailSender: emailSender);

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
    public async Task CreateInvoiceAsync_DoesNotCallEmailProviderInRequest()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var auditLog = new FakeAuditLogService();
        var emailSender = new ThrowingEmailSender();
        var service = CreateBillingService(db, auditLog, emailSender);

        var invoice = await service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            Status = InvoiceStatus.Sent,
            Currency = "GHS",
            Subtotal = 50m,
            TaxAmount = 0m
        }, performedBy: "admin@example.com");

        Assert.NotNull(invoice);
        Assert.Single(auditLog.Entries);
        Assert.Equal(AuditAction.InvoiceSent, auditLog.Entries[0].Action);
        Assert.Single(db.EmailOutboxMessages);
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
        var invoice = await service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            Status = InvoiceStatus.Sent,
            Currency = "USD",
            Subtotal = 100m,
            TaxAmount = 0m
        }, performedBy: "admin@example.com");

        var receipt = await service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            AmountPaid = 40m,
            PaymentMethod = PaymentMethod.BankTransfer,
            PaymentReference = "BANK-123"
        }, performedBy: "admin@example.com");

        var updatedInvoice = await service.GetInvoiceAsync(invoice.Id);

        Assert.Equal("RCP-", receipt.ReceiptNumber[..4]);
        Assert.Equal(40m, receipt.AmountPaid);
        Assert.Equal(InvoiceStatus.PartiallyPaid, updatedInvoice!.Status);
        Assert.Equal(40m, updatedInvoice.AmountPaid);
        Assert.Equal(60m, updatedInvoice.AmountDue);
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
        var invoice = await service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            Status = InvoiceStatus.Sent,
            Currency = "USD",
            Subtotal = 100m,
            TaxAmount = 10m
        }, performedBy: "admin@example.com");

        await service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            AmountPaid = 110m,
            PaymentMethod = PaymentMethod.Cash
        }, performedBy: "admin@example.com");

        var updatedInvoice = await service.GetInvoiceAsync(invoice.Id);

        Assert.Equal(InvoiceStatus.Paid, updatedInvoice!.Status);
        Assert.Equal(110m, updatedInvoice.AmountPaid);
        Assert.Equal(0m, updatedInvoice.AmountDue);
    }

    [Fact]
    public async Task RecordReceiptAsync_QueuesPaymentReceiptEmailWhenCustomerNotTracked()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var createService = CreateBillingService(db);
        var invoice = await createService.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            Status = InvoiceStatus.Sent,
            Currency = "USD",
            Subtotal = 100m,
            TaxAmount = 0m
        }, performedBy: "admin@example.com");

        // Simulate a fresh request scope: navigations are not already tracked.
        db.ChangeTracker.Clear();
        var recordService = CreateBillingService(db);

        await recordService.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            AmountPaid = 100m,
            PaymentMethod = PaymentMethod.BankTransfer,
            PaymentReference = "REF-1"
        }, performedBy: "admin@example.com");

        var message = Assert.Single(db.EmailOutboxMessages.Where(m => m.Kind == EmailDeliveryKind.PaymentReceipt));
        Assert.Equal("billing@acme.test", message.ToEmail);
        Assert.Contains("Receipt", message.Subject);
        Assert.Contains("$100.00", message.HtmlBody);
        Assert.Equal(invoice.Id, message.InvoiceId);
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
        IEmailSender? emailSender = null)
    {
        return new BillingService(
            db,
            auditLog ?? new FakeAuditLogService(),
            new EmailOutboxService(db),
            new EmailTemplateService());
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

    private sealed class FakeInvoicePdfGenerator : IInvoicePdfGenerator
    {
        public byte[] Generate(Invoice invoice, Customer customer, InvoiceLetterhead letterhead) =>
            "%PDF-1.4 fake-invoice"u8.ToArray();
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

    private sealed class CapturingEmailSender : IEmailSender
    {
        public List<CapturedEmail> Messages { get; } = [];

        public Task SendAsync(
            string toEmail,
            string subject,
            string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(new CapturedEmail(toEmail, subject, htmlBody, attachments?.ToList() ?? []));
            return Task.CompletedTask;
        }
    }

    private sealed record CapturedEmail(
        string ToEmail,
        string Subject,
        string HtmlBody,
        IReadOnlyList<EmailAttachment> Attachments);

    private sealed class ThrowingEmailSender : IEmailSender
    {
        public Task SendAsync(
            string toEmail,
            string subject,
            string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("SMTP server unavailable.");
        }
    }
}
