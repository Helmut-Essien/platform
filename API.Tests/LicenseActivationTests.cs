using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Api.Services.Email;
using Platform.Api.Services.Renewals;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;
using Xunit;
using static API.Tests.RenewalTests;

namespace API.Tests;

/// <summary>Paid activation invoices auto-activate Pending licenses (shared path with manual activation).</summary>
public class LicenseActivationTests
{
    internal sealed record Harness(AppDbContext Db, BillingService Billing, LicenseService Licenses, RecordingAudit Audit);

    internal static Harness Create(AppDbContext db, BillingSettings? settings = null,
        Platform.Api.Services.Renewals.PayLinkService? payLinks = null)
    {
        var audit = new RecordingAudit();
        var outbox = new EmailOutboxService(db);
        var templates = new EmailTemplateService();
        var keyDelivery = new LicenseKeyDeliveryService(db, outbox, CreateProtector(), templates);
        var billing = new BillingService(db, audit, outbox, templates, new NoopDeny(),
            billingSettings: Options.Create(settings ?? new BillingSettings()), keyDelivery: keyDelivery, payLinks: payLinks);
        var licenses = new LicenseService(db, billing, audit, keyDelivery, new NoopDeny(), outbox, templates,
            Options.Create(settings ?? new BillingSettings()), payLinks);
        return new Harness(db, billing, licenses, audit);
    }

    internal static EmailPayloadProtector CreateProtector() => new(
        Options.Create(new EmailSettings { Outbox = new EmailOutboxSettings { EncryptionKey = Convert.ToBase64String(new byte[32]) } }),
        new ConfigurationBuilder().Build(),
        new HostEnv());

    private sealed class HostEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "API.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    internal static async Task<(License License, InvoiceDto Invoice)> IssuePricedAsync(Harness h, decimal price = 1200m)
    {
        var seeded = await SeedLicenseAsync(h.Db, expiresAt: null, price: price, status: LicenseStatus.Pending);
        var invoice = await h.Billing.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = seeded.CustomerId,
            LicenseId = seeded.Id,
            Subtotal = price,
            Currency = null,
            ActivatesLicense = true
        }, "admin@example.com");
        invoice = await h.Billing.SendInvoiceAsync(invoice.Id, "admin@example.com");
        return (seeded, invoice);
    }

    internal static RecordReceiptRequest Pay(decimal amount, string key, DateTime? paidAt = null) => new()
    {
        IdempotencyKey = key,
        AmountPaid = amount,
        PaymentMethod = PaymentMethod.Cash,
        PaidAt = paidAt
    };

    private static Task<License> Reload(AppDbContext db, string id) =>
        db.Licenses.IgnoreQueryFilters().AsNoTracking().FirstAsync(l => l.Id == id);

    private static int KeyEmails(AppDbContext db) => db.EmailOutboxMessages.Count(m => m.Kind == EmailDeliveryKind.LicenseKey);

    [Fact]
    public async Task CreateAsync_WithPrice_BillsPriceAsActivationInvoice()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var seeded = await SeedLicenseAsync(db, null, status: LicenseStatus.Pending);
        db.Licenses.Remove(seeded);
        await db.SaveChangesAsync();
        var dto = await h.Licenses.CreateAsync(new CreateLicenseRequest
        {
            CustomerId = seeded.CustomerId,
            ServiceProductId = seeded.ServiceProductId,
            PlanName = "Pro",
            CreateInvoice = true,
            SendInvoice = false,
            InvoiceCurrency = null,
            Billing = new LicenseBillingDto { Amount = 500m, Currency = null, PeriodMonths = 6 }
        }, "admin@example.com");

        var invoice = await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.LicenseId == dto.Id);
        Assert.True(invoice.IsActivationInvoice);
        Assert.Equal(500m, invoice.Subtotal);
        Assert.Equal("GHS", invoice.Currency);
        Assert.Equal(6, invoice.ActivationPeriodMonths);
        Assert.Equal(LicenseStatus.Pending, dto.Status);
        Assert.Equal(invoice.Id, dto.ActivationInvoiceId);
    }

    [Fact]
    public async Task FullPayment_ActivatesDeliversKeyAndSetsExpiryFromPaymentDate()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var (license, invoice) = await IssuePricedAsync(h);
        var paidAt = DateTime.UtcNow.Date.AddHours(9);

        await h.Billing.RecordReceiptAsync(invoice.Id, Pay(1200m, "k1", paidAt), "admin@example.com");

        var stored = await Reload(db, license.Id);
        Assert.Equal(LicenseStatus.Active, stored.Status);
        Assert.NotNull(stored.LicenseKeyHash);
        Assert.Equal(PeriodMath.AddMonths(paidAt.Date, 12), stored.ExpiresAt);
        Assert.Equal(1, KeyEmails(db));
        Assert.Contains(AuditAction.LicenseAutoActivatedPaid, h.Audit.Actions);
        Assert.NotNull((await db.Invoices.IgnoreQueryFilters().FirstAsync(i => i.Id == invoice.Id)).ActivationAppliedAt);
    }

    [Fact]
    public async Task PartialPayment_DoesNotActivate_UntilFullyPaid()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var (license, invoice) = await IssuePricedAsync(h);

        await h.Billing.RecordReceiptAsync(invoice.Id, Pay(200m, "p1"), "admin@example.com");
        Assert.Equal(LicenseStatus.Pending, (await Reload(db, license.Id)).Status);
        Assert.Equal(0, KeyEmails(db));
        db.ChangeTracker.Clear(); // each payment arrives on its own request scope

        await h.Billing.RecordReceiptAsync(invoice.Id, Pay(1000m, "p2"), "admin@example.com");
        Assert.Equal(LicenseStatus.Active, (await Reload(db, license.Id)).Status);
        Assert.Equal(1, KeyEmails(db));
    }

    [Fact]
    public async Task ReplayedPayment_IsIdempotent_SingleKeyEmail()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var (license, invoice) = await IssuePricedAsync(h);

        await h.Billing.RecordReceiptAsync(invoice.Id, Pay(1200m, "paystack:ref1"), "system:paystack");
        var hash = (await Reload(db, license.Id)).LicenseKeyHash;
        await h.Billing.RecordReceiptAsync(invoice.Id, Pay(1200m, "paystack:ref1"), "system:reconcile");

        Assert.Equal(1, KeyEmails(db));
        Assert.Equal(hash, (await Reload(db, license.Id)).LicenseKeyHash);
        Assert.Single(h.Audit.Actions, a => a == AuditAction.LicenseAutoActivatedPaid);
    }

    [Fact]
    public async Task ManualActivationFirst_SkipsDuplicateInvoice_AndPaymentIsNoOp()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var (license, invoice) = await IssuePricedAsync(h);

        await h.Licenses.ActivateAsync(license.Id, new ActivateLicenseRequest
        {
            EmailLicenseKey = true, CreateInvoice = true, SendInvoice = false, Subtotal = 1200m
        }, "admin@example.com");
        Assert.Equal(1, await db.Invoices.IgnoreQueryFilters().CountAsync(i => i.LicenseId == license.Id));
        var active = await Reload(db, license.Id);

        await h.Billing.RecordReceiptAsync(invoice.Id, Pay(1200m, "k1"), "admin@example.com");

        var after = await Reload(db, license.Id);
        Assert.Equal(LicenseStatus.Active, after.Status);
        Assert.Equal(active.LicenseKeyHash, after.LicenseKeyHash);
        Assert.Equal(1, KeyEmails(db));
        Assert.Contains(AuditAction.LicenseActivationSkipped, h.Audit.Actions);
    }

    [Fact]
    public async Task SuspendedCustomer_PaymentDoesNotActivate()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var (license, invoice) = await IssuePricedAsync(h);
        var customer = await db.Customers.FirstAsync(c => c.Id == license.CustomerId);
        customer.IsSuspended = true;
        await db.SaveChangesAsync();

        await h.Billing.RecordReceiptAsync(invoice.Id, Pay(1200m, "k1"), "admin@example.com");

        Assert.Equal(LicenseStatus.Pending, (await Reload(db, license.Id)).Status);
        Assert.Equal(0, KeyEmails(db));
        Assert.Contains(AuditAction.LicenseActivationSkipped, h.Audit.Actions);
    }

    [Fact]
    public async Task RevokedLicense_PaymentDoesNotActivate()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var (license, invoice) = await IssuePricedAsync(h);
        var tracked = await db.Licenses.IgnoreQueryFilters().FirstAsync(l => l.Id == license.Id);
        tracked.Status = LicenseStatus.Revoked;
        await db.SaveChangesAsync();

        await h.Billing.RecordReceiptAsync(invoice.Id, Pay(1200m, "k1"), "admin@example.com");

        Assert.Equal(LicenseStatus.Revoked, (await Reload(db, license.Id)).Status);
        Assert.Equal(0, KeyEmails(db));
    }

    [Fact]
    public async Task SecondLiveActivationInvoice_Conflicts_ButAllowedAfterVoid()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var (license, invoice) = await IssuePricedAsync(h);
        var again = new CreateInvoiceRequest
        {
            CustomerId = license.CustomerId, LicenseId = license.Id, Subtotal = 1200m, ActivatesLicense = true
        };

        await Assert.ThrowsAsync<ConflictException>(() => h.Billing.CreateInvoiceAsync(again, "admin@example.com"));
        await h.Billing.VoidInvoiceAsync(invoice.Id, "admin@example.com");
        var replacement = await h.Billing.CreateInvoiceAsync(again, "admin@example.com");
        Assert.NotEqual(invoice.Id, replacement.Id);
    }

    [Fact]
    public async Task ActivationInvoice_RejectedForActiveLicenseOrZeroTotal()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var active = await SeedLicenseAsync(db, DateTime.UtcNow.AddMonths(3));
        await Assert.ThrowsAnyAsync<Exception>(() => h.Billing.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = active.CustomerId, LicenseId = active.Id, Subtotal = 10m, ActivatesLicense = true
        }, "admin@example.com"));

        var pending = await SeedLicenseAsync(db, null, status: LicenseStatus.Pending);
        await Assert.ThrowsAnyAsync<Exception>(() => h.Billing.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = pending.CustomerId, LicenseId = pending.Id, Subtotal = 0m, ActivatesLicense = true
        }, "admin@example.com"));
    }

    [Fact]
    public async Task Reversal_SuspendsActivatedLicense_AndRepaymentReactivates()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var (license, invoice) = await IssuePricedAsync(h);
        var receipt = await h.Billing.RecordReceiptAsync(invoice.Id, Pay(1200m, "k1"), "admin@example.com");

        await h.Billing.ReverseReceiptAsync(invoice.Id, receipt.Id,
            new ReverseReceiptRequest { IdempotencyKey = "r1", Reason = "chargeback" }, "admin@example.com");
        var reverted = await Reload(db, license.Id);
        Assert.Equal(LicenseStatus.Suspended, reverted.Status);
        Assert.Equal(invoice.Id, reverted.AutoSuspendedForOverdueInvoiceId);
        Assert.Contains(AuditAction.LicenseActivationReverted, h.Audit.Actions);

        await h.Billing.RecordReceiptAsync(invoice.Id, Pay(1200m, "k2"), "admin@example.com");
        Assert.Equal(LicenseStatus.Active, (await Reload(db, license.Id)).Status);
    }

    [Fact]
    public async Task VoidedActivationInvoice_CannotBePaid_LicenseStaysPending()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var (license, invoice) = await IssuePricedAsync(h);
        await h.Billing.VoidInvoiceAsync(invoice.Id, "admin@example.com");

        await Assert.ThrowsAnyAsync<Exception>(() => h.Billing.RecordReceiptAsync(invoice.Id, Pay(1200m, "k1"), "admin@example.com"));
        Assert.Equal(LicenseStatus.Pending, (await Reload(db, license.Id)).Status);
    }

    [Fact]
    public async Task PendingLicense_IsNotARenewalCandidate()
    {
        await using var db = CreateDb();
        await SeedLicenseAsync(db, DateTime.UtcNow.AddDays(3), status: LicenseStatus.Pending);
        var candidates = await RenewalInvoiceWorker.FindCandidatesAsync(db, new BillingSettings(), DateTime.UtcNow, [], default);
        Assert.Empty(candidates);
    }

    [Theory]
    [InlineData(null, "GHS")]
    [InlineData("", "GHS")]
    [InlineData("usd", "USD")]
    [InlineData("not-a-code", "GHS")]
    public void DefaultCurrency_FallsBackToGhs(string? configured, string expected) =>
        Assert.Equal(expected, BillingService.DefaultCurrencyOf(new BillingSettings { DefaultCurrency = configured! }));

    [Fact]
    public async Task CreateInvoice_BlankCurrency_UsesConfiguredDefault()
    {
        await using var db = CreateDb();
        var h = Create(db, new BillingSettings { DefaultCurrency = "NGN" });
        var license = await SeedLicenseAsync(db, DateTime.UtcNow.AddMonths(6));
        var invoice = await h.Billing.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = license.CustomerId, LicenseId = license.Id, Subtotal = 10m, Currency = " "
        }, "admin@example.com");
        Assert.Equal("NGN", invoice.Currency);
    }

    [Fact]
    public async Task PaidWhileCustomerSuspended_ManualActivationAppliesPaidTerm_WithoutNewInvoice()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var (license, invoice) = await IssuePricedAsync(h);
        var customer = await db.Customers.FirstAsync(c => c.Id == license.CustomerId);
        customer.IsSuspended = true;
        await db.SaveChangesAsync();
        var paidAt = DateTime.UtcNow.Date.AddDays(-2);
        await h.Billing.RecordReceiptAsync(invoice.Id, Pay(1200m, "k1", paidAt), "admin@example.com");
        customer.IsSuspended = false;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await h.Licenses.ActivateAsync(license.Id, new ActivateLicenseRequest { CreateInvoice = true, Subtotal = 1200m }, "admin@example.com");

        var stored = await Reload(db, license.Id);
        Assert.Equal(LicenseStatus.Active, stored.Status);
        Assert.Equal(PeriodMath.AddMonths(paidAt, 12), stored.ExpiresAt);
        Assert.Equal(1, await db.Invoices.IgnoreQueryFilters().CountAsync(i => i.LicenseId == license.Id));
        Assert.NotNull((await db.Invoices.IgnoreQueryFilters().FirstAsync(i => i.Id == invoice.Id)).ActivationAppliedAt);
    }

    [Fact]
    public void ExpiryAfterPayment_Rules()
    {
        var now = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
        var future = now.AddMonths(4);
        Assert.Equal(future, LicenseActivation.ExpiryAfterPayment(future, 12, now, now));
        Assert.Null(LicenseActivation.ExpiryAfterPayment(null, null, now, now));
        Assert.Equal(new DateTime(2027, 11, 2, 0, 0, 0, DateTimeKind.Utc), LicenseActivation.ExpiryAfterPayment(null, 12, now, now));
        Assert.Equal(new DateTime(2027, 5, 2, 0, 0, 0, DateTimeKind.Utc), LicenseActivation.ExpiryAfterPayment(now.AddDays(-10), 6, now, now));
    }
}
