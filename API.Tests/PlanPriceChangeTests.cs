using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Api.Services.Email;
using Platform.Api.Services.PriceChanges;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Dtos.ServiceProducts;
using Platform.Shared.Enums;
using Xunit;
using static API.Tests.RenewalTests;

namespace API.Tests;

/// <summary>Plan default prices and scheduled plan price changes (in-memory; locking is in the Postgres tests).</summary>
public class PlanPriceChangeTests
{
    internal static DateTime Today => DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);

    internal static PlanPriceChangeService PriceChanges(AppDbContext db, int noticeDays = 30) =>
        new(db, new AuditLogService(db), new EmailOutboxService(db), new EmailTemplateService(),
            Options.Create(new BillingSettings { PriceChangeNoticeDays = noticeDays }));

    internal static ProductPlanService PlansWithMail(AppDbContext db) =>
        new(db, new AuditLogService(db), new EmailOutboxService(db), new EmailTemplateService());

    internal sealed record Fixture(string ProductId, ProductPlanDto Plan);

    internal static async Task<Fixture> SeedPlanAsync(AppDbContext db, string displayName = "Workshop")
    {
        var productId = await ProductPlanTests.NewProductAsync(db, "NB-" + NUlid.Ulid.NewUlid().ToString()[^8..]);
        var plan = await ProductPlanTests.Plans(db).CreateAsync(productId, new CreateProductPlanRequest
        {
            Code = "workshop",
            DisplayName = displayName,
            DefaultPrice = new PlanPriceDto { Amount = 600m, TaxAmount = 90m, Currency = "GHS", PeriodMonths = 12 }
        }, "admin");
        return new Fixture(productId, plan);
    }

    internal static async Task<License> SeedLicenseAsync(AppDbContext db, string productId, string plan = "workshop",
        decimal? price = 600m, DateTime? expiresAt = null, LicenseStatus status = LicenseStatus.Active,
        bool complimentary = false, string customerName = "Acme Ltd", int months = 12)
    {
        var c = new Customer { Name = customerName, ContactEmail = $"c+{NUlid.Ulid.NewUlid()}@acme.test" };
        var l = new License
        {
            Customer = c, CustomerId = c.Id, ServiceProductId = productId, PlanName = plan, Status = status,
            ExpiresAt = expiresAt ?? Today.AddDays(60),
            BillingAmount = complimentary ? null : price,
            BillingTaxAmount = 0,
            BillingCurrency = complimentary || price is null ? null : "GHS",
            BillingPeriodMonths = months,
            BillingAnchorDay = (expiresAt ?? Today.AddDays(60)).Day,
            IsComplimentary = complimentary,
            AutoRenewInvoice = !complimentary,
            LicenseKeyLookupHash = NUlid.Ulid.NewUlid().ToString().PadRight(64, 'a')
        };
        db.AddRange(c, l);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return l;
    }

    internal static SchedulePlanPriceChangeRequest Schedule(DateTime effective, IEnumerable<string> ids, decimal amount = 750m,
        int? months = null, bool updateDefault = false) => new()
    {
        Amount = amount, TaxAmount = 0m, Currency = "ghs", PeriodMonths = months, EffectiveFrom = effective,
        LicenseIds = ids.ToList(), UpdatePlanDefaultPrice = updateDefault
    };

    private static Task<LicensePriceChange> PendingAsync(AppDbContext db, string licenseId)
    {
        db.ChangeTracker.Clear();
        return db.LicensePriceChanges.IgnoreQueryFilters()
            .SingleAsync(c => c.LicenseId == licenseId && c.Status == LicensePriceChangeStatus.Pending);
    }

    private static Task<int> MailCountAsync(AppDbContext db, EmailDeliveryKind kind, string licenseId) =>
        db.EmailOutboxMessages.CountAsync(m => m.Kind == kind && m.LicenseId == licenseId);

    // ----- plan default price -------------------------------------------------------------------------

    [Fact]
    public async Task PlanDefaultPrice_IsStored_NullMeansUnchanged_AndClearRemovesIt()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        Assert.Equal(600m, f.Plan.DefaultPrice!.Amount);
        Assert.Equal("GHS", f.Plan.DefaultPrice.Currency);

        // The active switch (and older clients) send no price: it stays.
        var toggled = await ProductPlanTests.Plans(db).UpdateAsync(f.ProductId, f.Plan.Id,
            new UpdateProductPlanRequest { DisplayName = "Workshop", IsActive = false }, "admin");
        Assert.Equal(600m, toggled.DefaultPrice!.Amount);

        var changed = await ProductPlanTests.Plans(db).UpdateAsync(f.ProductId, f.Plan.Id, new UpdateProductPlanRequest
        {
            DisplayName = "Workshop",
            DefaultPrice = new PlanPriceDto { Amount = 700m, TaxAmount = 0m, Currency = " usd ", PeriodMonths = 1 }
        }, "admin");
        Assert.Equal(700m, changed.DefaultPrice!.Amount);
        Assert.Equal("USD", changed.DefaultPrice.Currency);
        Assert.Equal(1, changed.DefaultPrice.PeriodMonths);

        var cleared = await ProductPlanTests.Plans(db).UpdateAsync(f.ProductId, f.Plan.Id,
            new UpdateProductPlanRequest { DisplayName = "Workshop", ClearDefaultPrice = true }, "admin");
        Assert.Null(cleared.DefaultPrice);
    }

    [Theory]
    [InlineData(0, 0, "GHS", 12)]
    [InlineData(10.001, 0, "GHS", 12)]
    [InlineData(10, -1, "GHS", 12)]
    [InlineData(10, 0, "CEDI", 12)]
    [InlineData(10, 0, "GHS", 37)]
    public async Task PlanDefaultPrice_FollowsTheLicensePriceRules(double amount, double tax, string currency, int months)
    {
        await using var db = CreateDb();
        var productId = await ProductPlanTests.NewProductAsync(db, "P1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProductPlanTests.Plans(db).CreateAsync(productId,
            new CreateProductPlanRequest
            {
                Code = "basic", DisplayName = "Basic",
                DefaultPrice = new PlanPriceDto { Amount = (decimal)amount, TaxAmount = (decimal)tax, Currency = currency, PeriodMonths = months }
            }, "admin"));
        Assert.Equal(0, await db.ProductPlans.CountAsync());
    }

    [Fact]
    public async Task ChangingThePlanDefaultPrice_NeverTouchesExistingLicenses()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        var license = await SeedLicenseAsync(db, f.ProductId, price: 600m);
        await ProductPlanTests.Plans(db).UpdateAsync(f.ProductId, f.Plan.Id, new UpdateProductPlanRequest
        {
            DisplayName = "Workshop", DefaultPrice = new PlanPriceDto { Amount = 900m, Currency = "GHS" }
        }, "admin");
        db.ChangeTracker.Clear();
        Assert.Equal(600m, (await db.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == license.Id)).BillingAmount);
        Assert.Empty(await db.LicensePriceChanges.IgnoreQueryFilters().ToListAsync());
    }

    // ----- preview ------------------------------------------------------------------------------------

    [Fact]
    public async Task Preview_ListsThePlansLicenses_FlagsTheOnesThatCantChange_AndCountsRevoked()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        var paid = await SeedLicenseAsync(db, f.ProductId, customerName: "A paid");
        var free = await SeedLicenseAsync(db, f.ProductId, complimentary: true, customerName: "B free");
        var unpriced = await SeedLicenseAsync(db, f.ProductId, price: null, customerName: "C unpriced");
        var same = await SeedLicenseAsync(db, f.ProductId, price: 750m, customerName: "D same");
        await SeedLicenseAsync(db, f.ProductId, status: LicenseStatus.Revoked, customerName: "E revoked");
        await SeedLicenseAsync(db, f.ProductId, plan: "starter", customerName: "F other plan");

        var preview = await PriceChanges(db).PreviewAsync(f.ProductId, f.Plan.Id,
            new PlanPriceChangeRequest { Amount = 750m, Currency = "GHS", EffectiveFrom = Today.AddDays(90) });

        Assert.Equal(["A paid", "B free", "C unpriced", "D same"], preview.Rows.Select(r => r.CustomerName));
        Assert.Equal(1, preview.RevokedCount);
        Assert.True(preview.Rows.Single(r => r.LicenseId == paid.Id).Eligible);
        Assert.Equal("Complimentary (free)", preview.Rows.Single(r => r.LicenseId == free.Id).IneligibleReason);
        Assert.False(preview.Rows.Single(r => r.LicenseId == unpriced.Id).Eligible);
        Assert.Equal("Already at this price", preview.Rows.Single(r => r.LicenseId == same.Id).IneligibleReason);
        Assert.False(preview.NoticeSentOnConfirm); // 90 days out, notice window is 30
        Assert.Empty(await db.LicensePriceChanges.IgnoreQueryFilters().ToListAsync()); // preview changes nothing
    }

    [Fact]
    public async Task Preview_FlagsAnInvoiceAlreadyIssuedForAPeriodFromTheEffectiveDate()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        var expiry = Today.AddDays(10);
        var license = await SeedLicenseAsync(db, f.ProductId, expiresAt: expiry);
        await CreateBilling(db).CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);

        var preview = await PriceChanges(db).PreviewAsync(f.ProductId, f.Plan.Id,
            new PlanPriceChangeRequest { Amount = 750m, EffectiveFrom = Today.AddDays(5) });
        Assert.True(preview.Rows.Single().NextInvoiceAlreadyIssued);
        Assert.True(preview.NoticeSentOnConfirm);
    }

    [Theory]
    [InlineData(0, 1)]       // price must be above zero
    [InlineData(750, -1)]    // yesterday
    [InlineData(750, 3000)]  // more than 5 years out
    public async Task Schedule_RejectsBadInput(double amount, int days)
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        var license = await SeedLicenseAsync(db, f.ProductId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id,
            Schedule(Today.AddDays(days), [license.Id], (decimal)amount), "admin"));
        Assert.Empty(await db.LicensePriceChanges.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Schedule_NeedsLicensesOrADefaultUpdate_AndCapsTheBatch()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id,
            Schedule(Today.AddDays(40), []), "admin"));
        var tooMany = Enumerable.Range(0, PlanPriceChangeService.MaxScheduleLicenses + 1).Select(i => $"id{i}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id,
            Schedule(Today.AddDays(40), tooMany), "admin"));
        await Assert.ThrowsAsync<NotFoundException>(() => PriceChanges(db).ScheduleAsync(f.ProductId, "nope",
            Schedule(Today.AddDays(40), ["x"]), "admin"));
    }

    // ----- schedule -----------------------------------------------------------------------------------

    [Fact]
    public async Task Schedule_StoresAPendingChangePerChosenLicense_LeavesTheirPriceAlone_AndSkipsTheRest()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        var a = await SeedLicenseAsync(db, f.ProductId);
        var b = await SeedLicenseAsync(db, f.ProductId);
        var optedOut = await SeedLicenseAsync(db, f.ProductId);
        var free = await SeedLicenseAsync(db, f.ProductId, complimentary: true);
        var otherProduct = await SeedLicenseAsync(db, await ProductPlanTests.NewProductAsync(db, "OTHER"));

        var result = await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id,
            Schedule(Today.AddDays(90), [a.Id, b.Id, free.Id, otherProduct.Id, a.Id]), "admin");

        Assert.Equal(2, result.ScheduledCount);
        Assert.Equal(2, result.SkippedCount);
        Assert.Equal(0, result.NoticesQueuedCount); // outside the 30-day window: the worker sends them later
        Assert.False(result.PlanDefaultPriceUpdated);
        var pa = await PendingAsync(db, a.Id);
        Assert.Equal(750m, pa.NewAmount);
        Assert.Equal("GHS", pa.NewCurrency);
        Assert.Equal(600m, pa.OldAmount);
        Assert.Equal("workshop", pa.PlanCode);
        Assert.Null(pa.NoticeQueuedAt);
        Assert.False(await db.LicensePriceChanges.IgnoreQueryFilters().AnyAsync(c => c.LicenseId == optedOut.Id || c.LicenseId == free.Id));
        Assert.Equal(600m, (await db.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == a.Id)).BillingAmount);
        Assert.Equal(2, await db.AuditLogs.CountAsync(x => x.Action == AuditAction.LicensePriceChangeScheduled));
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.Action == AuditAction.PlanPriceChangeScheduled));
        Assert.Equal(0, await db.EmailOutboxMessages.CountAsync());
        Assert.Equal(600m, (await db.ProductPlans.SingleAsync()).DefaultAmount);
    }

    [Fact]
    public async Task Schedule_InsideTheNoticeWindow_SendsTheNoticeNow_WithTheDisplayNameEncoded()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db, "Pro & <Plus>");
        var license = await SeedLicenseAsync(db, f.ProductId);

        var result = await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id,
            Schedule(Today.AddDays(10), [license.Id], updateDefault: true), "admin");

        Assert.Equal(1, result.NoticesQueuedCount);
        Assert.True(result.PlanDefaultPriceUpdated);
        var mail = await db.EmailOutboxMessages.SingleAsync(m => m.Kind == EmailDeliveryKind.PriceChangeNotice);
        Assert.Contains("Pro &amp; &lt;Plus&gt;", mail.HtmlBody);
        Assert.DoesNotContain("<Plus>", mail.HtmlBody);
        Assert.Contains("750.00", mail.HtmlBody);
        Assert.Contains("600.00", mail.HtmlBody);
        Assert.NotNull((await PendingAsync(db, license.Id)).NoticeQueuedAt);
        var plan = await db.ProductPlans.SingleAsync();
        Assert.Equal(750m, plan.DefaultAmount);
        Assert.Equal(12, plan.DefaultPeriodMonths); // no new period given: the plan keeps its own

        // The worker doesn't send it again.
        Assert.Equal(0, await PriceChanges(db).QueueDueNoticesAsync(DateTime.UtcNow));
        Assert.Equal(1, await MailCountAsync(db, EmailDeliveryKind.PriceChangeNotice, license.Id));
    }

    [Fact]
    public async Task Notice_WhenTheNextInvoiceIsAlreadyIssued_SaysTheNewPriceStartsTheRenewalAfter()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        var expiry = Today.AddDays(10);
        var license = await SeedLicenseAsync(db, f.ProductId, expiresAt: expiry);
        await CreateBilling(db).CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);
        db.ChangeTracker.Clear();

        await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id, Schedule(expiry, [license.Id]), "admin");

        var notice = await db.EmailOutboxMessages.SingleAsync(m => m.Kind == EmailDeliveryKind.PriceChangeNotice && m.LicenseId == license.Id);
        Assert.Contains("already been issued at the current price", notice.HtmlBody);
    }

    [Fact]
    public async Task Schedule_OnlyUpdatingTheDefault_ChangesNoLicense()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        await SeedLicenseAsync(db, f.ProductId);
        var result = await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id,
            Schedule(Today.AddDays(10), [], amount: 800m, months: 6, updateDefault: true), "admin");
        Assert.Equal(0, result.ScheduledCount);
        Assert.True(result.PlanDefaultPriceUpdated);
        var plan = await db.ProductPlans.SingleAsync();
        Assert.Equal((800m, 6), (plan.DefaultAmount!.Value, plan.DefaultPeriodMonths!.Value));
        Assert.Empty(await db.LicensePriceChanges.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Schedule_ReplacesAnEarlierPendingChange_AndReAnnouncesWhenTheOldOneWasAnnounced()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        var license = await SeedLicenseAsync(db, f.ProductId);
        await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id, Schedule(Today.AddDays(10), [license.Id]), "admin");

        // New date far outside the window: the customer already holds a notice, so they are told again now.
        var result = await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id,
            Schedule(Today.AddDays(200), [license.Id], amount: 800m), "admin");

        Assert.Equal(1, result.ReplacedCount);
        Assert.Equal(1, result.NoticesQueuedCount);
        var pending = await PendingAsync(db, license.Id);
        Assert.Equal(800m, pending.NewAmount);
        var old = await db.LicensePriceChanges.IgnoreQueryFilters().SingleAsync(c => c.Status == LicensePriceChangeStatus.Cancelled);
        Assert.Equal(LicensePriceChangeOps.ReasonReplaced, old.CancelReason);
        Assert.Equal(2, await MailCountAsync(db, EmailDeliveryKind.PriceChangeNotice, license.Id));
        Assert.Equal(0, await MailCountAsync(db, EmailDeliveryKind.PriceChangeCancelled, license.Id));
    }

    [Fact]
    public async Task Schedule_TheSameChangeTwice_IsANoOp_AndSendsNoSecondNotice()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        var license = await SeedLicenseAsync(db, f.ProductId);
        await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id, Schedule(Today.AddDays(10), [license.Id]), "admin");

        var again = await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id, Schedule(Today.AddDays(10), [license.Id]), "admin");

        Assert.Equal(0, again.ScheduledCount);
        Assert.Equal(1, again.SkippedCount);
        Assert.Equal(0, again.ReplacedCount);
        Assert.Equal(1, await db.LicensePriceChanges.IgnoreQueryFilters().CountAsync(c => c.LicenseId == license.Id));
        Assert.Equal(1, await MailCountAsync(db, EmailDeliveryKind.PriceChangeNotice, license.Id));
        var row = Assert.Single((await PriceChanges(db).PreviewAsync(f.ProductId, f.Plan.Id, Schedule(Today.AddDays(10), []))).Rows);
        Assert.False(row.Eligible);
        Assert.Equal("Already scheduled at this price and date", row.IneligibleReason);
    }

    [Fact]
    public async Task Preview_ListsLicensesAlreadyScheduledAtThisPriceLast()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        var first = await SeedLicenseAsync(db, f.ProductId, customerName: "Alpha");
        var second = await SeedLicenseAsync(db, f.ProductId, customerName: "Beta");
        await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id, Schedule(Today.AddDays(90), [first.Id]), "admin");

        var rows = (await PriceChanges(db).PreviewAsync(f.ProductId, f.Plan.Id, Schedule(Today.AddDays(90), []))).Rows;

        Assert.Equal([second.Id, first.Id], rows.Select(r => r.LicenseId).ToArray());
    }

    [Fact]
    public async Task Schedule_ForASuspendedCustomer_WaitsWithTheNoticeUntilTheyAreReinstated()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        var license = await SeedLicenseAsync(db, f.ProductId);
        var customer = await db.Customers.SingleAsync(c => c.Id == license.CustomerId);
        customer.IsSuspended = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id, Schedule(Today.AddDays(10), [license.Id]), "admin");
        Assert.Equal(1, result.ScheduledCount);
        Assert.Equal(0, result.NoticesQueuedCount);
        Assert.Equal(0, await PriceChanges(db).QueueDueNoticesAsync(DateTime.UtcNow));

        customer = await db.Customers.SingleAsync(c => c.Id == license.CustomerId);
        customer.IsSuspended = false;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(1, await PriceChanges(db).QueueDueNoticesAsync(DateTime.UtcNow));
        Assert.Equal(1, await MailCountAsync(db, EmailDeliveryKind.PriceChangeNotice, license.Id));
    }

    // ----- renewal applies it -------------------------------------------------------------------------

    [Fact]
    public async Task RenewalInvoice_ForAPeriodFromTheEffectiveDate_BillsTheNewPrice_AndTheLicenseKeepsIt()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        var expiry = Today.AddDays(10);
        var license = await SeedLicenseAsync(db, f.ProductId, expiresAt: expiry);
        await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id,
            Schedule(expiry, [license.Id], amount: 750m, months: 6), "admin");
        db.ChangeTracker.Clear();

        var invoice = await CreateBilling(db).CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);

        Assert.Equal(750m, invoice!.Subtotal);
        db.ChangeTracker.Clear();
        var saved = await db.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == license.Id);
        Assert.Equal((750m, "GHS", 6), (saved.BillingAmount!.Value, saved.BillingCurrency, saved.BillingPeriodMonths));
        var change = await db.LicensePriceChanges.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(LicensePriceChangeStatus.Applied, change.Status);
        Assert.Equal(invoice.Id, change.AppliedInvoiceId);
        Assert.Equal(6, (await db.Invoices.IgnoreQueryFilters().SingleAsync()).RenewalPeriodMonths);
    }

    [Fact]
    public async Task RenewalInvoice_ForAPeriodBeforeTheEffectiveDate_KeepsTheOldPrice_AndTheChangeWaits()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        var expiry = Today.AddDays(10);
        var license = await SeedLicenseAsync(db, f.ProductId, expiresAt: expiry);
        await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id, Schedule(expiry.AddDays(1), [license.Id]), "admin");
        db.ChangeTracker.Clear();

        var invoice = await CreateBilling(db).CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);

        Assert.Equal(600m, invoice!.Subtotal);
        Assert.Equal(LicensePriceChangeStatus.Pending, (await PendingAsync(db, license.Id)).Status);
    }

    [Fact]
    public async Task AnInvoiceAlreadyIssued_IsNeverChanged_ByALaterPriceChange()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        var expiry = Today.AddDays(10);
        var license = await SeedLicenseAsync(db, f.ProductId, expiresAt: expiry);
        var issued = await CreateBilling(db).CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);
        db.ChangeTracker.Clear();

        await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id, Schedule(Today, [license.Id]), "admin");

        db.ChangeTracker.Clear();
        Assert.Equal(600m, (await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == issued!.Id)).Subtotal);
        Assert.Equal(LicensePriceChangeStatus.Pending, (await PendingAsync(db, license.Id)).Status);
    }

    [Fact]
    public async Task RenewalInvoice_DoesNotApplyAChangeForAnotherPlan()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        var expiry = Today.AddDays(10);
        var license = await SeedLicenseAsync(db, f.ProductId, expiresAt: expiry);
        await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id, Schedule(expiry, [license.Id]), "admin");
        // Moved by a path that doesn't cancel (direct data fix): the renewal must still not bill the other price.
        db.ChangeTracker.Clear();
        var tracked = await db.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == license.Id);
        tracked.PlanName = "starter";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var invoice = await CreateBilling(db).CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);

        Assert.Equal(600m, invoice!.Subtotal);
        var change = await db.LicensePriceChanges.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(LicensePriceChangeStatus.Cancelled, change.Status);
        Assert.Equal(LicensePriceChangeOps.ReasonPlanChanged, change.CancelReason);
    }

    // ----- notices ------------------------------------------------------------------------------------

    [Fact]
    public async Task NoticeWorker_SendsOncePerChange_OnlyInsideTheWindow_AndNotWhenTurnedOff()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        var soon = await SeedLicenseAsync(db, f.ProductId);
        var later = await SeedLicenseAsync(db, f.ProductId);
        await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id, Schedule(Today.AddDays(45), [soon.Id]), "admin");
        await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id, Schedule(Today.AddDays(120), [later.Id]), "admin");

        Assert.Equal(0, await PriceChanges(db, noticeDays: 0).QueueDueNoticesAsync(DateTime.UtcNow));
        Assert.Equal(0, await PriceChanges(db).QueueDueNoticesAsync(DateTime.UtcNow));
        // 20 days later the first one is inside its 30-day window.
        Assert.Equal(1, await PriceChanges(db).QueueDueNoticesAsync(DateTime.UtcNow.AddDays(20)));
        Assert.Equal(0, await PriceChanges(db).QueueDueNoticesAsync(DateTime.UtcNow.AddDays(20)));
        Assert.Equal(1, await MailCountAsync(db, EmailDeliveryKind.PriceChangeNotice, soon.Id));
        Assert.Equal(0, await MailCountAsync(db, EmailDeliveryKind.PriceChangeNotice, later.Id));
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AuditAction.LicensePriceChangeNoticeQueued));
    }

    // ----- cancel and interactions --------------------------------------------------------------------

    [Fact]
    public async Task Cancel_StopsTheChange_TellsTheCustomerOnlyIfNotified_AndCantRunTwice()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        var told = await SeedLicenseAsync(db, f.ProductId);
        var untold = await SeedLicenseAsync(db, f.ProductId);
        await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id, Schedule(Today.AddDays(10), [told.Id]), "admin");
        await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id, Schedule(Today.AddDays(90), [untold.Id]), "admin");
        var toldChange = await PendingAsync(db, told.Id);
        var untoldChange = await PendingAsync(db, untold.Id);

        var cancelled = await PriceChanges(db).CancelAsync(told.Id, toldChange.Id, "admin");
        await PriceChanges(db).CancelAsync(untold.Id, untoldChange.Id, "admin");

        Assert.Equal(LicensePriceChangeStatus.Cancelled, cancelled.Status);
        Assert.Equal("admin", cancelled.CancelledBy);
        Assert.Equal(1, await MailCountAsync(db, EmailDeliveryKind.PriceChangeCancelled, told.Id));
        Assert.Equal(0, await MailCountAsync(db, EmailDeliveryKind.PriceChangeCancelled, untold.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PriceChanges(db).CancelAsync(told.Id, toldChange.Id, "admin"));
        await Assert.ThrowsAsync<NotFoundException>(() => PriceChanges(db).CancelAsync(untold.Id, toldChange.Id, "admin"));
        var history = await PriceChanges(db).ListForLicenseAsync(told.Id);
        Assert.Single(history);
    }

    [Fact]
    public async Task ManualEdit_ADifferentPriceOrPlanCancelsThePendingChange_OtherEditsKeepIt()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        await ProductPlanTests.AddPlanAsync(db, f.ProductId, "starter", "Starter");
        var keep = await SeedLicenseAsync(db, f.ProductId);
        var reprice = await SeedLicenseAsync(db, f.ProductId);
        var move = await SeedLicenseAsync(db, f.ProductId);
        await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id,
            Schedule(Today.AddDays(10), [keep.Id, reprice.Id, move.Id]), "admin");
        var licenses = LicenseDeviceReleaseTests.CreateLicenses(db);
        db.ChangeTracker.Clear();

        // Same price, new expiry and devices; the dialog always sends the billing block.
        var kept = await licenses.UpdateAsync(keep.Id, new UpdateLicenseRequest
        {
            PlanName = "workshop", ExpiresAt = Today.AddDays(70), MaxDevices = 2,
            Billing = new LicenseBillingDto { Amount = 600m, Currency = "ghs", PeriodMonths = 12, AutoRenewInvoice = false }
        }, "admin");
        Assert.NotNull(kept.PendingPriceChange);

        db.ChangeTracker.Clear();
        var repriced = await licenses.UpdateAsync(reprice.Id, new UpdateLicenseRequest
        {
            PlanName = "workshop",
            Billing = new LicenseBillingDto { Amount = 650m, Currency = "GHS", PeriodMonths = 12 }
        }, "admin");
        Assert.Null(repriced.PendingPriceChange);

        db.ChangeTracker.Clear();
        var moved = await licenses.UpdateAsync(move.Id, new UpdateLicenseRequest { PlanName = "starter" }, "admin");
        Assert.Null(moved.PendingPriceChange);

        db.ChangeTracker.Clear();
        var reasons = await db.LicensePriceChanges.IgnoreQueryFilters()
            .Where(c => c.Status == LicensePriceChangeStatus.Cancelled)
            .ToDictionaryAsync(c => c.LicenseId, c => c.CancelReason);
        Assert.Equal(LicensePriceChangeOps.ReasonManualPrice, reasons[reprice.Id]);
        Assert.Equal(LicensePriceChangeOps.ReasonPlanChanged, reasons[move.Id]);
        Assert.False(reasons.ContainsKey(keep.Id));
        // Both were announced (inside the window), so both customers hear it is off.
        Assert.Equal(1, await MailCountAsync(db, EmailDeliveryKind.PriceChangeCancelled, reprice.Id));
        Assert.Equal(1, await MailCountAsync(db, EmailDeliveryKind.PriceChangeCancelled, move.Id));
        var mail = await db.EmailOutboxMessages.SingleAsync(m => m.Kind == EmailDeliveryKind.PriceChangeCancelled && m.LicenseId == reprice.Id);
        Assert.Contains("650.00", mail.HtmlBody); // shows the price that now applies
    }

    [Fact]
    public async Task MakingTheLicenseFree_OrRevokingIt_CancelsThePendingChange()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        var free = await SeedLicenseAsync(db, f.ProductId);
        var revoked = await SeedLicenseAsync(db, f.ProductId);
        await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id, Schedule(Today.AddDays(10), [free.Id, revoked.Id]), "admin");
        var licenses = LicenseDeviceReleaseTests.CreateLicenses(db);
        db.ChangeTracker.Clear();

        await licenses.MakeComplimentaryAsync(free.Id, new MakeComplimentaryRequest(), "admin");
        db.ChangeTracker.Clear();
        await licenses.RevokeAsync(revoked.Id, "admin");

        db.ChangeTracker.Clear();
        Assert.False(await db.LicensePriceChanges.IgnoreQueryFilters().AnyAsync(c => c.Status == LicensePriceChangeStatus.Pending));
        Assert.Equal(1, await MailCountAsync(db, EmailDeliveryKind.PriceChangeCancelled, free.Id));
        Assert.Equal(0, await MailCountAsync(db, EmailDeliveryKind.PriceChangeCancelled, revoked.Id));
        var cancelled = await db.EmailOutboxMessages.SingleAsync(m => m.Kind == EmailDeliveryKind.PriceChangeCancelled && m.LicenseId == free.Id);
        Assert.Contains("now complimentary", cancelled.HtmlBody);
    }

    [Fact]
    public async Task Remap_OntoAnotherPlan_CancelsThePendingChange()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        await ProductPlanTests.AddPlanAsync(db, f.ProductId, "production", "Production");
        var license = await SeedLicenseAsync(db, f.ProductId);
        await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id, Schedule(Today.AddDays(90), [license.Id]), "admin");
        db.ChangeTracker.Clear();

        await PlansWithMail(db).RemapAsync(f.ProductId,
            new RemapProductPlanRequest { FromPlanName = "workshop", ToCode = "production" }, "admin");

        db.ChangeTracker.Clear();
        var change = await db.LicensePriceChanges.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(LicensePriceChangeStatus.Cancelled, change.Status);
        Assert.Equal(0, await MailCountAsync(db, EmailDeliveryKind.PriceChangeCancelled, license.Id)); // never announced
    }

    [Fact]
    public async Task LicenseGetAndList_ShowThePendingChange()
    {
        await using var db = CreateDb();
        var f = await SeedPlanAsync(db);
        var license = await SeedLicenseAsync(db, f.ProductId);
        var other = await SeedLicenseAsync(db, f.ProductId);
        await PriceChanges(db).ScheduleAsync(f.ProductId, f.Plan.Id, Schedule(Today.AddDays(90), [license.Id]), "admin");
        var licenses = LicenseDeviceReleaseTests.CreateLicenses(db);
        db.ChangeTracker.Clear();

        var one = await licenses.GetAsync(license.Id, includeSuspendedCustomers: true);
        Assert.Equal(750m, one!.PendingPriceChange!.NewAmount);
        var page = await licenses.ListAsync(includeSuspendedCustomers: true);
        Assert.NotNull(page.Items.Single(l => l.Id == license.Id).PendingPriceChange);
        Assert.Null(page.Items.Single(l => l.Id == other.Id).PendingPriceChange);
    }

    [Fact]
    public void PriceChangeEmails_AreEncoded_AndReadWell()
    {
        var templates = new EmailTemplateService();
        var customer = new Customer { Name = "<script>Tailor</script>", ContactEmail = "a@b.test" };
        var product = new ServiceProduct { Name = "Needle & Base", Code = "NB" };
        var license = new License { CustomerId = customer.Id, ServiceProductId = product.Id, PlanName = "workshop",
            ExpiresAt = new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc), BillingAmount = 600m, BillingCurrency = "GHS", BillingPeriodMonths = 12 };
        var change = new LicensePriceChange
        {
            LicenseId = license.Id, BatchId = "b", PlanCode = "workshop", NewCurrency = "GHS", NewAmount = 750m, NewTaxAmount = 0,
            OldAmount = 600m, OldCurrency = "GHS", OldPeriodMonths = 12, EffectiveFrom = new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var notice = templates.PriceChangeNotice(customer, product, license, change, "Workshop");
        Assert.Contains("1 March 2027", notice.Subject);
        Assert.Contains("Needle &amp; Base (Workshop)", notice.Html);
        Assert.DoesNotContain("<script>", notice.Html);
        Assert.Contains("GH₵750.00 per year", notice.Html);
        Assert.Contains("period starting 1 March 2027", notice.Html);

        var cancelled = templates.PriceChangeCancelled(customer, product, license, change, "Workshop");
        Assert.Contains("cancelled", cancelled.Subject);
        Assert.Contains("GH₵600.00 per year", cancelled.Html);
    }
}
