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
    public async Task RecordReceiptAsync_RejectsIdempotencyKeyReusedWithDifferentAmount()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);
        var invoice = await CreateSentInvoiceAsync(service, customer.Id, 100m);

        await service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "same-key",
            AmountPaid = 40m,
            PaymentMethod = PaymentMethod.BankTransfer,
            PaymentReference = "REF-A"
        }, performedBy: "admin@example.com");

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
            {
                IdempotencyKey = "same-key",
                AmountPaid = 50m,
                PaymentMethod = PaymentMethod.BankTransfer,
                PaymentReference = "REF-A"
            }, performedBy: "admin@example.com"));

        Assert.Contains("different payment", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateInvoiceAsync_RejectsPaidStatusAndTreatsZeroTotalAsPaid()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                CustomerId = customer.Id,
                Status = InvoiceStatus.Paid,
                Currency = "USD",
                Subtotal = 10m,
                TaxAmount = 0m
            }, performedBy: "admin@example.com"));
        Assert.Contains("Draft or Sent", ex.Message);

        var zero = await service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            Status = InvoiceStatus.Sent,
            Currency = "USD",
            Subtotal = 0m,
            TaxAmount = 0m,
            DueDate = DateTime.UtcNow.AddDays(-2)
        }, performedBy: "admin@example.com");

        Assert.Equal(InvoiceStatus.Paid, zero.Status);
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
        Assert.Contains("Payment received", message.Subject);
        Assert.Contains("receipt", message.Subject, StringComparison.OrdinalIgnoreCase);
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
            Status = InvoiceStatus.Sent,
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
    public async Task ReverseReceiptAsync_ResuspendsLicenseWhenAutoSuspendEnabled()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var product = await SeedProductAsync(db);
        var license = new License
        {
            CustomerId = customer.Id,
            ServiceProductId = product.Id,
            PlanName = "Pro",
            Status = LicenseStatus.Active
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();

        var denyList = new FakeDenyListService();
        var service = CreateBillingService(
            db,
            denyList: denyList,
            lifecycle: new Platform.Api.Configuration.LifecycleSettings { AutoSuspendOnOverdue = true });
        var invoice = await service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            LicenseId = license.Id,
            ServiceProductId = product.Id,
            Status = InvoiceStatus.Sent,
            Currency = "USD",
            Subtotal = 80m,
            TaxAmount = 0m,
            DueDate = DateTime.UtcNow.AddDays(-2)
        }, performedBy: "admin@example.com");

        var receipt = await service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "pay-then-reverse",
            AmountPaid = 80m,
            PaymentMethod = PaymentMethod.Cash
        }, performedBy: "admin@example.com");

        await service.ReverseReceiptAsync(invoice.Id, receipt.Id, new ReverseReceiptRequest
        {
            IdempotencyKey = "reverse-overdue",
            Reason = "bounced"
        }, performedBy: "admin@example.com");

        var reloaded = await db.Licenses.IgnoreQueryFilters().FirstAsync(l => l.Id == license.Id);
        Assert.Equal(LicenseStatus.Suspended, reloaded.Status);
        Assert.Equal(invoice.Id, reloaded.AutoSuspendedForOverdueInvoiceId);
        Assert.Contains(license.Id, denyList.DeniedLicenseIds);
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

    [Fact]
    public async Task GetInvoiceStatsAsync_AggregatesAllInvoicesPerCurrency()
    {
        // B1/E7: aggregates must cover every invoice (not one page) and never mix currencies.
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);

        var usdPaid = await CreateSentInvoiceAsync(service, customer.Id, 100m);
        await service.RecordReceiptAsync(usdPaid.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "stats-1",
            AmountPaid = 100m,
            PaymentMethod = PaymentMethod.Cash
        }, performedBy: "admin@example.com");
        var usdPartial = await CreateSentInvoiceAsync(service, customer.Id, 50m);
        await service.RecordReceiptAsync(usdPartial.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "stats-2",
            AmountPaid = 20m,
            PaymentMethod = PaymentMethod.Cash
        }, performedBy: "admin@example.com");
        // Well over one page of unpaid invoices.
        for (var i = 0; i < 120; i++)
            await CreateSentInvoiceAsync(service, customer.Id, 1m);
        await service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            Status = InvoiceStatus.Sent,
            Currency = "EUR",
            Subtotal = 40m
        }, performedBy: "admin@example.com");
        var overdue = await db.Invoices.FirstAsync(i => i.Id == usdPartial.Id);
        // PartiallyPaid invoice counts as unpaid; mark one other invoice overdue.
        var other = await db.Invoices.Where(i => i.Status == InvoiceStatus.Sent && i.Currency == "USD").FirstAsync();
        other.Status = InvoiceStatus.Overdue;
        await db.SaveChangesAsync();

        var stats = await service.GetInvoiceStatsAsync();

        var usd = Assert.Single(stats.Currencies, c => c.Currency == "USD");
        var eur = Assert.Single(stats.Currencies, c => c.Currency == "EUR");
        Assert.Equal(120m, usd.TotalRevenue);
        Assert.Equal(30m + 120m, usd.Outstanding);
        Assert.Equal(120m, usd.PaidThisMonth);
        Assert.Equal(2, usd.PaidThisMonthCount);
        Assert.Equal(1, usd.OverdueCount);
        Assert.Equal(0m, eur.TotalRevenue);
        Assert.Equal(40m, eur.Outstanding);
        Assert.Equal(122, stats.UnpaidCount);
        Assert.Equal(1, stats.OverdueCount);
        Assert.NotNull(overdue);
    }

    [Fact]
    public async Task ListInvoicesAsync_FiltersByStatusSearchAndIssueDateOnTheServer()
    {
        await using var db = CreateDbContext();
        var acme = await SeedCustomerAsync(db);
        var globex = new Customer { Name = "Globex", ContactEmail = "g@globex.test" };
        db.Customers.Add(globex);
        await db.SaveChangesAsync();
        var service = CreateBillingService(db);

        var a = await CreateSentInvoiceAsync(service, acme.Id, 10m);
        var b = await CreateSentInvoiceAsync(service, globex.Id, 20m);
        var c = await CreateSentInvoiceAsync(service, globex.Id, 30m);
        (await db.Invoices.FirstAsync(i => i.Id == c.Id)).Status = InvoiceStatus.Overdue;
        (await db.Invoices.FirstAsync(i => i.Id == a.Id)).IssueDate = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        var bySearch = await service.ListInvoicesAsync(search: "glob");
        Assert.Equal(2, bySearch.TotalCount);

        var byStatus = await service.ListInvoicesAsync(status: InvoiceStatus.Overdue);
        Assert.Equal(c.Id, Assert.Single(byStatus.Items).Id);

        var byNumber = await service.ListInvoicesAsync(search: b.InvoiceNumber.ToLowerInvariant());
        Assert.Equal(b.Id, Assert.Single(byNumber.Items).Id);

        // Inclusive calendar-day bounds.
        var january = await service.ListInvoicesAsync(
            issuedFrom: new DateTime(2026, 1, 15), issuedTo: new DateTime(2026, 1, 15));
        Assert.Equal(a.Id, Assert.Single(january.Items).Id);

        var none = await service.ListInvoicesAsync(issuedTo: new DateTime(2026, 1, 14));
        Assert.Empty(none.Items);
        Assert.Equal(0, none.TotalCount);
    }

    [Fact]
    public async Task ListInvoicesAsync_FiltersByCustomerId()
    {
        await using var db = CreateDbContext();
        var acme = await SeedCustomerAsync(db);
        var globex = new Customer { Name = "Globex", ContactEmail = "g@globex.test" };
        db.Customers.Add(globex);
        await db.SaveChangesAsync();
        var service = CreateBillingService(db);

        await CreateSentInvoiceAsync(service, acme.Id, 10m);
        var g1 = await CreateSentInvoiceAsync(service, globex.Id, 20m);
        var g2 = await CreateSentInvoiceAsync(service, globex.Id, 30m);

        var forGlobex = await service.ListInvoicesAsync(customerId: globex.Id);
        Assert.Equal(2, forGlobex.TotalCount);
        Assert.Equal(new[] { g1.Id, g2.Id }.OrderBy(x => x), forGlobex.Items.Select(i => i.Id).OrderBy(x => x));

        var unknown = await service.ListInvoicesAsync(customerId: "no-such-customer");
        Assert.Equal(0, unknown.TotalCount);
    }

    [Fact]
    public async Task VoidInvoiceAsync_RejectsInvoiceWithPostedPayment()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);
        var invoice = await CreateSentInvoiceAsync(service, customer.Id, 50m);
        await service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "void-guard",
            AmountPaid = 10m,
            PaymentMethod = PaymentMethod.Cash
        }, performedBy: "admin@example.com");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.VoidInvoiceAsync(invoice.Id, "admin@example.com"));

        Assert.Equal(InvoiceStatus.PartiallyPaid, (await db.Invoices.AsNoTracking().FirstAsync(i => i.Id == invoice.Id)).Status);
    }

    [Theory]
    [InlineData("10.005")]
    [InlineData("0.001")]
    [InlineData("20000000000000")]
    public async Task RecordReceiptAsync_RejectsSubCentAndOutOfRangeAmounts(string amount)
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);
        var invoice = await CreateSentInvoiceAsync(service, customer.Id, 50m);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
            {
                IdempotencyKey = "precision",
                AmountPaid = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture),
                PaymentMethod = PaymentMethod.Cash
            }, performedBy: "admin@example.com"));

        Assert.Empty(db.PaymentTransactions);
    }

    [Fact]
    public async Task RecordReceiptAsync_AcceptsTwoDecimalAmountsAndReplaysIdempotently()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);
        var invoice = await CreateSentInvoiceAsync(service, customer.Id, 50.10m);
        var request = new RecordReceiptRequest
        {
            IdempotencyKey = "cents",
            AmountPaid = 20.10m,
            PaymentMethod = PaymentMethod.Cash
        };

        var first = await service.RecordReceiptAsync(invoice.Id, request, performedBy: "admin@example.com");
        var replay = await service.RecordReceiptAsync(invoice.Id, request, performedBy: "admin@example.com");

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(30m, (await service.GetInvoiceAsync(invoice.Id))!.AmountDue);
    }

    [Theory]
    [InlineData("100.001", "0")]
    [InlineData("100", "0.005")]
    public async Task CreateInvoiceAsync_RejectsSubCentSubtotalOrTax(string subtotal, string tax)
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            Status = InvoiceStatus.Sent,
            Currency = "USD",
            Subtotal = decimal.Parse(subtotal, System.Globalization.CultureInfo.InvariantCulture),
            TaxAmount = decimal.Parse(tax, System.Globalization.CultureInfo.InvariantCulture)
        }, performedBy: "admin@example.com"));
    }

    [Theory]
    [InlineData("US")]
    [InlineData("US1")]
    [InlineData("US$")]
    [InlineData("")]
    public async Task CreateInvoiceAsync_RejectsMalformedCurrency(string currency)
    {
        // E11
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            Status = InvoiceStatus.Sent,
            Currency = currency,
            Subtotal = 10m
        }, performedBy: "admin@example.com"));
    }

    [Fact]
    public async Task CreateInvoiceAsync_NormalizesCurrencyCase()
    {
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);

        var invoice = await service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            Status = InvoiceStatus.Sent,
            Currency = " ghs ",
            Subtotal = 10m
        }, performedBy: "admin@example.com");

        Assert.Equal("GHS", invoice.Currency);
    }

    [Fact]
    public async Task RecordReceiptAsync_RejectsFuturePaymentDate()
    {
        // E10
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);
        var invoice = await CreateSentInvoiceAsync(service, customer.Id, 50m);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
            {
                IdempotencyKey = "future",
                AmountPaid = 10m,
                PaymentMethod = PaymentMethod.Cash,
                PaidAt = DateTime.UtcNow.AddDays(10)
            }, performedBy: "admin@example.com"));

        var receipt = await service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "past",
            AmountPaid = 10m,
            PaymentMethod = PaymentMethod.Cash,
            PaidAt = DateTime.UtcNow.AddDays(-3)
        }, performedBy: "admin@example.com");
        Assert.True(receipt.PaidAt < DateTime.UtcNow.AddDays(-2));
    }

    [Fact]
    public async Task SendInvoiceAsync_RefusesFirstSendOfDraftForSuspendedCustomer()
    {
        // E12
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);
        var draft = await service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            Status = InvoiceStatus.Draft,
            Currency = "USD",
            Subtotal = 25m
        }, performedBy: "admin@example.com");

        customer.IsSuspended = true;
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SendInvoiceAsync(draft.Id, performedBy: "admin@example.com"));
        Assert.Equal(InvoiceStatus.Draft, (await db.Invoices.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Fact]
    public async Task DocumentNumbers_FollowTheHighestExistingNumberEvenWithGaps()
    {
        // P10
        await using var db = CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var service = CreateBillingService(db);
        var year = DateTime.UtcNow.Year;
        db.Invoices.Add(new Invoice
        {
            CustomerId = customer.Id,
            InvoiceNumber = $"INV-{year}-00007",
            Status = InvoiceStatus.Draft,
            Currency = "USD"
        });
        await db.SaveChangesAsync();

        var invoice = await CreateSentInvoiceAsync(service, customer.Id, 10m);

        Assert.Equal($"INV-{year}-00008", invoice.InvoiceNumber);
    }

    [Theory]
    [InlineData(null, "INV-2026-00001")]
    [InlineData("INV-2026-00009", "INV-2026-00010")]
    [InlineData("INV-2026-99999", "INV-2026-100000")]
    [InlineData("garbage", "INV-2026-00001")]
    public void NextDocumentNumber_IncrementsLastNumber(string? last, string expected) =>
        Assert.Equal(expected, BillingService.NextDocumentNumber("INV-2026-", last));

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
        ILicenseDenyListService? denyList = null,
        Platform.Api.Configuration.LifecycleSettings? lifecycle = null)
    {
        return new BillingService(
            db,
            auditLog ?? new FakeAuditLogService(),
            new EmailOutboxService(db),
            new EmailTemplateService(),
            denyList ?? new FakeDenyListService(),
            lifecycle is null ? null : Microsoft.Extensions.Options.Options.Create(lifecycle));
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
        public List<string> DeniedLicenseIds { get; } = [];

        public Task DenyLicenseAsync(string licenseId, CancellationToken cancellationToken = default)
        {
            DeniedLicenseIds.Add(licenseId);
            return Task.CompletedTask;
        }

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
