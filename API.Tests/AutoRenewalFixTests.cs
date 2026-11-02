using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
using static API.Tests.LicenseActivationTests;
using static API.Tests.RenewalTests;

namespace API.Tests;

/// <summary>Regression tests for the feat/auto-renewal review fixes.</summary>
public class AutoRenewalFixTests
{
    private static Task<License> ReloadLicense(AppDbContext db, string id)
    {
        db.ChangeTracker.Clear();
        return db.Licenses.IgnoreQueryFilters().AsNoTracking().FirstAsync(l => l.Id == id);
    }

    private static Task<Invoice> ReloadInvoice(AppDbContext db, string id)
    {
        db.ChangeTracker.Clear();
        return db.Invoices.IgnoreQueryFilters().AsNoTracking().FirstAsync(i => i.Id == id);
    }

    private static async Task ExpireLinkAsync(AppDbContext db, string invoiceId)
    {
        var tracked = await db.Invoices.IgnoreQueryFilters().FirstAsync(i => i.Id == invoiceId);
        tracked.PayLinkExpiresAt = DateTime.UtcNow.AddDays(-1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static Task<ReceiptDto> Refund(BillingService billing, string invoiceId, string receiptId, string key) =>
        billing.ReverseReceiptAsync(invoiceId, receiptId, new ReverseReceiptRequest { IdempotencyKey = key, Reason = "refund" }, "admin");

    // ---------------------------------------------------------------- fix 2: working pay link after a refund

    [Fact]
    public async Task Refund_RenewsExpiredPayLink_AndSuspensionEmailCarriesIt()
    {
        await using var db = CreateDb();
        var h = Create(db, payLinks: CreatePayLinks());
        var (license, invoice) = await IssuePricedAsync(h);
        var receipt = await h.Billing.RecordReceiptAsync(invoice.Id, Pay(1200m, "k1"), "admin");
        // Paid links are cut to a day; simulate that day having passed before the refund.
        await ExpireLinkAsync(db, invoice.Id);

        await Refund(h.Billing, invoice.Id, receipt.Id, "r1");

        var stored = await ReloadInvoice(db, invoice.Id);
        Assert.True(stored.PayLinkExpiresAt > DateTime.UtcNow.AddDays(7));
        Assert.NotNull(stored.PayLinkNonce);
        var notice = await db.EmailOutboxMessages.Where(m => m.Kind == EmailDeliveryKind.Suspended && m.LicenseId == license.Id).SingleAsync();
        Assert.Contains("/pay/", notice.HtmlBody);
    }

    [Fact]
    public async Task Refund_OfRenewal_RenewsExpiredPayLink()
    {
        await using var db = CreateDb();
        var expiry = DateTime.UtcNow.Date.AddDays(10);
        var license = await SeedLicenseAsync(db, expiry);
        var h = Create(db, payLinks: CreatePayLinks());
        var invoice = await h.Billing.CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);
        var receipt = await PayInFullAsync(h.Billing, invoice!);
        await ExpireLinkAsync(db, invoice!.Id);

        await Refund(h.Billing, invoice.Id, receipt.Id, "r1");

        Assert.True((await ReloadInvoice(db, invoice.Id)).PayLinkExpiresAt > DateTime.UtcNow.AddDays(7));
    }

    [Fact]
    public async Task ManualSuspend_ForHeldInvoice_RenewsExpiredPayLink()
    {
        await using var db = CreateDb();
        var expiry = DateTime.UtcNow.Date.AddDays(10);
        var license = await SeedLicenseAsync(db, expiry);
        var h = Create(db, payLinks: CreatePayLinks());
        var invoice = await h.Billing.CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);
        await ExpireLinkAsync(db, invoice!.Id);

        await h.Licenses.SuspendAsync(license.Id, "admin", autoSuspendedForOverdueInvoiceId: invoice!.Id);

        Assert.True((await ReloadInvoice(db, invoice.Id)).PayLinkExpiresAt > DateTime.UtcNow.AddDays(7));
        var notice = await db.EmailOutboxMessages.Where(m => m.Kind == EmailDeliveryKind.Suspended).SingleAsync();
        Assert.Contains("/pay/", notice.HtmlBody);
    }

    // ---------------------------------------------------------------- fix 3: repeated refunds

    [Fact]
    public async Task ActivationInvoice_SecondRefundAfterRepayment_SuspendsAgain()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var (license, invoice) = await IssuePricedAsync(h);
        var first = await h.Billing.RecordReceiptAsync(invoice.Id, Pay(1200m, "k1"), "admin");
        await Refund(h.Billing, invoice.Id, first.Id, "r1");
        Assert.Equal(LicenseStatus.Suspended, (await ReloadLicense(db, license.Id)).Status);

        var second = await h.Billing.RecordReceiptAsync(invoice.Id, Pay(1200m, "k2"), "admin");
        Assert.Equal(LicenseStatus.Active, (await ReloadLicense(db, license.Id)).Status);
        Assert.NotNull((await ReloadInvoice(db, invoice.Id)).ActivationAppliedAt);

        await Refund(h.Billing, invoice.Id, second.Id, "r2");
        var after = await ReloadLicense(db, license.Id);
        Assert.Equal(LicenseStatus.Suspended, after.Status);
        Assert.Equal(invoice.Id, after.AutoSuspendedForOverdueInvoiceId);

        // And a third payment restores it again.
        await h.Billing.RecordReceiptAsync(invoice.Id, Pay(1200m, "k3"), "admin");
        Assert.Equal(LicenseStatus.Active, (await ReloadLicense(db, license.Id)).Status);
    }

    [Fact]
    public async Task RenewalInvoice_RepeatedRefunds_RevertEachTime()
    {
        await using var db = CreateDb();
        var expiry = DateTime.UtcNow.Date.AddDays(10);
        var license = await SeedLicenseAsync(db, expiry);
        var billing = CreateBilling(db);
        var invoice = await billing.CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);
        var extended = PeriodMath.AddMonths(expiry, 12);

        var p1 = await PayInFullAsync(billing, invoice!, "p1");
        Assert.Equal(extended, (await ReloadLicense(db, license.Id)).ExpiresAt);
        await Refund(billing, invoice!.Id, p1.Id, "r1");
        Assert.Equal(expiry, (await ReloadLicense(db, license.Id)).ExpiresAt);

        var p2 = await PayInFullAsync(billing, invoice, "p2");
        Assert.Equal(extended, (await ReloadLicense(db, license.Id)).ExpiresAt);
        await Refund(billing, invoice.Id, p2.Id, "r2");
        Assert.Equal(expiry, (await ReloadLicense(db, license.Id)).ExpiresAt);
    }

    // ---------------------------------------------------------------- fix 4: a late activation payment buys a full period

    [Fact]
    public async Task ActivationPayment_WithShortRemainingExpiry_GetsFullPeriodFromPaymentDay_AndNoImmediateRenewal()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var (license, invoice) = await IssuePricedAsync(h);
        var tracked = await db.Licenses.IgnoreQueryFilters().FirstAsync(l => l.Id == license.Id);
        tracked.ExpiresAt = DateTime.UtcNow.Date.AddDays(5); // admin-set expiry almost reached when the customer pays
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var paidAt = DateTime.UtcNow.Date.AddHours(9);

        await h.Billing.RecordReceiptAsync(invoice.Id, Pay(1200m, "k1", paidAt), "admin");

        var stored = await ReloadLicense(db, license.Id);
        Assert.Equal(LicenseStatus.Active, stored.Status);
        Assert.Equal(PeriodMath.AddMonths(paidAt.Date, 12), stored.ExpiresAt);
        Assert.Equal(paidAt.Day, stored.BillingAnchorDay);
        // Nothing to renew yet: the new term is a year away.
        Assert.Empty(await RenewalInvoiceWorker.FindCandidatesAsync(db, new BillingSettings(), DateTime.UtcNow, [], default));
    }

    // ---------------------------------------------------------------- fix 5: trial invoice description follows the new end

    [Fact]
    public async Task ExtendTrial_RewritesTrialInvoiceDescription()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        var dto = await h.Licenses.CreateAsync(TrialAndComplimentaryTests.TrialRequest(c, p, 1200m), "admin");
        var invoice = await h.Billing.CreateRenewalInvoiceAsync(dto.Id, dto.ExpiresAt, BillingService.RenewalActor);
        var oldStart = dto.ExpiresAt!.Value.ToString("dd MMM yyyy");
        Assert.Contains(oldStart, invoice!.Description);

        var newEnd = dto.ExpiresAt.Value.AddDays(10);
        await h.Licenses.ExtendTrialAsync(dto.Id, new ExtendTrialRequest { TrialEndsAt = newEnd }, "admin");

        var stored = await ReloadInvoice(db, invoice.Id);
        var license = await ReloadLicense(db, dto.Id);
        var expected = BillingService.RenewalDescription(license.PlanName, true, newEnd,
            PeriodMath.AddMonths(newEnd, license.BillingPeriodMonths, license.BillingAnchorDay));
        Assert.Equal(expected, stored.Description);
        Assert.DoesNotContain(oldStart, stored.Description);
    }

    // ---------------------------------------------------------------- low: brand name, reply-to, issuer

    [Fact]
    public void Templates_UseCachedBrandName_EncodedInHtml()
    {
        var cache = new BrandNameCache();
        cache.Set(new InvoiceBrandProfile { CompanyName = "Acme & Sons" });
        var templates = new EmailTemplateService(null, cache);
        var (subject, html) = templates.PasswordReset("admin@example.com");
        Assert.Equal("Reset your Acme & Sons admin password", subject);
        Assert.Contains("Acme &amp; Sons", html);
        Assert.Contains("The Acme &amp; Sons team", html);
        Assert.DoesNotContain("Acme & Sons", html); // never unencoded in the body

        // Blank / legacy placeholder names fall back to the built-in name.
        cache.Set(new InvoiceBrandProfile { CompanyName = BrandDefaults.LegacyPlaceholderName });
        Assert.Contains(BrandDefaults.CompanyName, templates.PasswordReset("a@b.test").Subject);
        Assert.Equal(BrandDefaults.CompanyName, EmailBrandContact.CompanyNameOrDefault("  "));
    }

    [Fact]
    public async Task BrandService_SavingProfile_RefreshesCache()
    {
        await using var db = CreateDb();
        var cache = new BrandNameCache();
        var service = new InvoiceBrandService(db, cache);
        await service.UpdateAsync(new Platform.Shared.Dtos.Settings.UpdateInvoiceBrandRequest { CompanyName = "  New Name Ltd " });
        Assert.Equal("New Name Ltd", cache.Current);
    }

    [Fact]
    public async Task BrandProfile_LatestSavedRowWins_EverywhereAndStably()
    {
        await using var db = CreateDb();
        db.InvoiceBrandProfiles.AddRange(
            new InvoiceBrandProfile { CompanyName = "Old Co", UpdatedAt = DateTime.UtcNow.AddDays(-5) },
            new InvoiceBrandProfile { CompanyName = "New Co", UpdatedAt = DateTime.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var cache = new BrandNameCache();
        var service = new InvoiceBrandService(db, cache);

        Assert.Equal("New Co", (await service.GetProfileEntityAsync()).CompanyName);
        Assert.Equal("New Co", cache.Current);
        await service.UpdateAsync(new Platform.Shared.Dtos.Settings.UpdateInvoiceBrandRequest { CompanyName = "Newer Co" });
        db.ChangeTracker.Clear();
        Assert.Equal("Newer Co", (await service.GetProfileEntityAsync()).CompanyName);
    }

    [Theory]
    [InlineData("support@example.com", "support@example.com")]
    [InlineData("  support@example.com ", "support@example.com")]
    [InlineData("not-an-email", null)]
    [InlineData("Support <support@example.com>", null)]
    [InlineData("a@example.com, b@example.com", null)]
    [InlineData("user@localhost", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ReplyTo_InvalidValuesAreIgnored(string? configured, string? expected) =>
        Assert.Equal(expected, EmailReplyTo.Resolve(configured, NullLogger.Instance));

    // ---------------------------------------------------------------- low: expiry reminder picks the right invoice and recipient

    [Fact]
    public async Task ExpiryReminder_UsesCurrentPeriodInvoice_AndBillingContact()
    {
        var services = new ServiceCollection();
        var database = NUlid.Ulid.NewUlid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(database));
        services.AddScoped<IEmailOutboxService, EmailOutboxService>();
        services.AddSingleton<EmailTemplateService>();
        services.AddSingleton(CreatePayLinks());
        await using var provider = services.BuildServiceProvider();

        var expiresAt = DateTime.UtcNow.Date.AddDays(7);
        string currentNumber, otherNumber;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = new Customer
            {
                Name = "Acme", ContactEmail = "main@acme.test",
                BillingEmail = "billing@acme.test", TechnicalEmail = "tech@acme.test"
            };
            var product = new ServiceProduct { Name = "Hostel", Code = "HOSTEL" };
            var license = new License
            {
                Customer = customer, CustomerId = customer.Id,
                ServiceProduct = product, ServiceProductId = product.Id,
                PlanName = "Pro", Status = LicenseStatus.Active, ExpiresAt = expiresAt,
                BillingAmount = 100m, BillingCurrency = "GHS", BillingPeriodMonths = 12
            };
            Invoice Make(string number, DateTime periodStart, DateTime createdAt) => new()
            {
                CustomerId = customer.Id, LicenseId = license.Id, ServiceProductId = product.Id,
                InvoiceNumber = number, Status = InvoiceStatus.Sent, Currency = "GHS",
                Subtotal = 100m, TotalAmount = 100m, RenewalForExpiresAt = periodStart,
                CreatedAt = createdAt, UpdatedAt = createdAt
            };
            // The newer invoice is for a different period and must not be offered next to this expiry.
            var current = Make("INV-CUR-1", expiresAt, DateTime.UtcNow.AddDays(-2));
            var other = Make("INV-OTH-2", expiresAt.AddYears(-1), DateTime.UtcNow.AddDays(-1));
            db.AddRange(license, current, other);
            await db.SaveChangesAsync();
            currentNumber = current.InvoiceNumber;
            otherNumber = other.InvoiceNumber;
        }

        var worker = new LicenseExpiryReminderWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new LifecycleSettings { ExpiryReminderDays = 30 }),
            NullLogger<LicenseExpiryReminderWorker>.Instance);
        await worker.EnqueueRemindersAsync(CancellationToken.None);

        await using var assertScope = provider.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var message = Assert.Single(await assertDb.EmailOutboxMessages.ToListAsync());
        Assert.Equal("billing@acme.test", message.ToEmail);
        Assert.Contains("/pay/", message.HtmlBody);
        Assert.Contains(currentNumber, message.HtmlBody);
        Assert.DoesNotContain(otherNumber, message.HtmlBody);
    }

    // ---------------------------------------------------------------- low: deny list follows the commit

    [Fact]
    public async Task OverdueSuspension_DeniesLicense()
    {
        await using var db = CreateDb();
        var expiry = DateTime.UtcNow.Date.AddDays(-3);
        var license = await SeedLicenseAsync(db, DateTime.UtcNow.Date.AddDays(20));
        var h = Create(db);
        var invoice = await h.Billing.CreateRenewalInvoiceAsync(license.Id, null, BillingService.RenewalActor);
        var tracked = await db.Invoices.IgnoreQueryFilters().FirstAsync(i => i.Id == invoice!.Id);
        tracked.DueDate = expiry;
        tracked.Status = InvoiceStatus.Overdue;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var deny = new RecordingDeny();
        var suspended = await OverdueInvoiceLifecycleWorker.TrySuspendForOverdueAsync(
            db, h.Licenses, h.Audit, invoice!.Id, license.Id, license.CustomerId, CancellationToken.None, deny);

        Assert.True(suspended);
        Assert.Contains(license.Id, deny.Denied);
    }

    private sealed class RecordingDeny : ILicenseDenyListService
    {
        public List<string> Denied { get; } = [];
        public Task DenyLicenseAsync(string licenseId, CancellationToken cancellationToken = default)
        {
            Denied.Add(licenseId);
            return Task.CompletedTask;
        }
        public Task DenyCustomerLicensesAsync(string customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsDeniedAsync(string licenseId, CancellationToken cancellationToken = default) => Task.FromResult(Denied.Contains(licenseId));
        public Task ClearLicenseDenyAsync(string licenseId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearCustomerDenyAsync(string customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
