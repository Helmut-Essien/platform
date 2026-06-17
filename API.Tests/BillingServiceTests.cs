using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
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
        var service = new BillingService(db, auditLog);

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
    public async Task CreateInvoiceAsync_DoesNotSendEmailInCurrentBillingFlow()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var auditLog = new FakeAuditLogService();
        var service = new BillingService(db, auditLog);

        await service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            Status = InvoiceStatus.Sent,
            Currency = "GHS",
            Subtotal = 50m,
            TaxAmount = 0m
        }, performedBy: "admin@example.com");

        Assert.DoesNotContain(auditLog.Entries, e => e.Action == AuditAction.ReceiptRecorded);
        Assert.Single(auditLog.Entries);
        Assert.Equal(AuditAction.InvoiceSent, auditLog.Entries[0].Action);
    }

    [Fact]
    public async Task CreateInvoiceAsync_RejectsSuspendedCustomer()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db, isSuspended: true);
        var service = new BillingService(db, new FakeAuditLogService());

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
        var service = new BillingService(db, auditLog);
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
        var service = new BillingService(db, new FakeAuditLogService());
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

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options;

        return new AppDbContext(options);
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
