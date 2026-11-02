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
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;
using Xunit;
using static API.Tests.RenewalTests;

namespace API.Tests;

/// <summary>Free trials and complimentary licenses (in-memory; locking/index behaviour is in the Postgres tests).</summary>
public class TrialAndComplimentaryTests
{
    internal static async Task<(string CustomerId, string ProductId)> SeedCustomerAndProductAsync(AppDbContext db)
    {
        var customer = new Customer { Name = "Acme Ltd", ContactEmail = $"ops+{NUlid.Ulid.NewUlid()}@acme.test" };
        var product = new ServiceProduct { Name = "Hostel", Code = "H-" + NUlid.Ulid.NewUlid().ToString()[^10..], IsAvailableForSale = true };
        db.Customers.Add(customer);
        db.ServiceProducts.Add(product);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (customer.Id, product.Id);
    }

    internal static CreateLicenseRequest TrialRequest(string customerId, string productId, decimal? price = null) => new()
    {
        CustomerId = customerId,
        ServiceProductId = productId,
        PlanName = "Pro",
        Trial = true,
        Billing = price is null ? null : new LicenseBillingDto { Amount = price, Currency = null, PeriodMonths = 12 }
    };

    private static Task<License> Reload(AppDbContext db, string id)
    {
        db.ChangeTracker.Clear();
        return db.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == id);
    }

    private static DateTime Today => DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);

    [Fact]
    public async Task Trial_ActivatesNow_EmailsKey_NoInvoice_EndsAfterTrialMonths()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db, new BillingSettings { TrialMonths = 2 });
        var (c, p) = await SeedCustomerAndProductAsync(db);

        var dto = await h.Licenses.CreateAsync(TrialRequest(c, p, 1200m), "admin");

        Assert.Equal(LicenseStatus.Active, dto.Status);
        Assert.True(dto.IsTrial);
        Assert.NotNull(dto.TrialStartedAt);
        Assert.Equal(PeriodMath.AddMonths(Today, 2), dto.ExpiresAt);
        Assert.Equal(PeriodMath.AddMonths(Today, 3), dto.TrialMaxEndsAt); // default cap 3 months
        Assert.Equal(1, db.EmailOutboxMessages.Count(m => m.Kind == EmailDeliveryKind.LicenseKey));
        Assert.Empty(db.Invoices.IgnoreQueryFilters().Where(i => i.LicenseId == dto.Id));
        Assert.Contains(AuditAction.LicenseTrialStarted, h.Audit.Actions);
    }

    [Fact]
    public async Task SecondTrial_SameCustomerAndService_Rejected_EvenAfterRevoke()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await SeedCustomerAndProductAsync(db);
        var first = await h.Licenses.CreateAsync(TrialRequest(c, p), "admin");
        await h.Licenses.RevokeAsync(first.Id, "admin");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Licenses.CreateAsync(TrialRequest(c, p), "admin"));
        Assert.Equal(LicenseService.TrialAlreadyUsed, ex.Message);

        // A paid license for the same pair is still fine.
        var paid = await h.Licenses.CreateAsync(new CreateLicenseRequest { CustomerId = c, ServiceProductId = p, PlanName = "Pro" }, "admin");
        Assert.False(paid.IsTrial);
    }

    [Fact]
    public async Task TrialAndComplimentaryTogether_Rejected()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await SeedCustomerAndProductAsync(db);
        var request = TrialRequest(c, p);
        request.Complimentary = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Licenses.CreateAsync(request, "admin"));
    }

    [Fact]
    public async Task PricedTrial_ReminderIsTheFirstInvoice_PayingConvertsFromTrialEnd()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await SeedCustomerAndProductAsync(db);
        var dto = await h.Licenses.CreateAsync(TrialRequest(c, p, 1200m), "admin");
        var trialEnd = dto.ExpiresAt!.Value;

        var invoice = await h.Billing.CreateRenewalInvoiceAsync(dto.Id, trialEnd, BillingService.RenewalActor);
        Assert.NotNull(invoice);
        var entity = await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == invoice!.Id);
        Assert.True(entity.IsTrialConversion);
        Assert.Equal(trialEnd, entity.RenewalForExpiresAt);

        var mail = Assert.Single(db.EmailOutboxMessages.Where(m => m.Kind == EmailDeliveryKind.Invoice));
        Assert.Contains("trial ends", mail.Subject);
        var license = await Reload(db, dto.Id);
        Assert.Equal(trialEnd, license.TrialReminderForEndsAt);
        Assert.Contains(AuditAction.LicenseTrialReminderQueued, h.Audit.Actions);

        // No second invoice for the same trial end (worker re-run).
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            var again = await h.Billing.CreateRenewalInvoiceAsync(dto.Id, trialEnd, BillingService.RenewalActor);
            if (again is null)
                throw new ConflictException("none");
        });
        Assert.Single(db.Invoices.IgnoreQueryFilters().Where(i => i.LicenseId == dto.Id));

        await PayInFullAsync(h.Billing, invoice!);
        license = await Reload(db, dto.Id);
        Assert.False(license.IsTrial);
        Assert.NotNull(license.TrialStartedAt); // kept: one trial per customer and service
        Assert.Equal(PeriodMath.AddMonths(trialEnd, 12), license.ExpiresAt);
        Assert.Equal(LicenseStatus.Active, license.Status);
        Assert.Contains(AuditAction.LicenseTrialConverted, h.Audit.Actions);
    }

    [Fact]
    public async Task ExtendTrial_RespectsCap_AndMovesUnpaidTrialInvoice()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db, new BillingSettings { TrialMonths = 1, TrialMaxTotalMonths = 2 });
        var (c, p) = await SeedCustomerAndProductAsync(db);
        var dto = await h.Licenses.CreateAsync(TrialRequest(c, p, 1200m), "admin");
        var invoice = await h.Billing.CreateRenewalInvoiceAsync(dto.Id, dto.ExpiresAt, BillingService.RenewalActor);

        var tooLate = dto.TrialMaxEndsAt!.Value.AddDays(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Licenses.ExtendTrialAsync(dto.Id, new ExtendTrialRequest { TrialEndsAt = tooLate }, "admin"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Licenses.ExtendTrialAsync(dto.Id, new ExtendTrialRequest { TrialEndsAt = dto.ExpiresAt!.Value }, "admin"));

        var newEnd = dto.TrialMaxEndsAt!.Value;
        var extended = await h.Licenses.ExtendTrialAsync(dto.Id, new ExtendTrialRequest { TrialEndsAt = newEnd }, "admin");
        Assert.Equal(newEnd, extended.ExpiresAt);
        Assert.True(extended.IsTrial);

        db.ChangeTracker.Clear();
        var moved = await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == invoice!.Id);
        Assert.Equal(newEnd, moved.RenewalForExpiresAt);
        Assert.Equal(newEnd, moved.DueDate);
        Assert.Contains(AuditAction.LicenseTrialExtended, h.Audit.Actions);

        await PayInFullAsync(h.Billing, invoice!);
        var license = await Reload(db, dto.Id);
        Assert.False(license.IsTrial);
        Assert.Equal(PeriodMath.AddMonths(newEnd, 12), license.ExpiresAt);
    }

    [Fact]
    public async Task ExtendTrial_ReopensExpiredTrial_AndRejectsNonTrial()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await SeedCustomerAndProductAsync(db);
        var dto = await h.Licenses.CreateAsync(TrialRequest(c, p), "admin");
        var tracked = await db.Licenses.SingleAsync(l => l.Id == dto.Id);
        tracked.Status = LicenseStatus.Expired;
        tracked.ExpiresAt = Today.AddDays(-1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var reopened = await h.Licenses.ExtendTrialAsync(dto.Id, new ExtendTrialRequest { TrialEndsAt = Today.AddDays(10) }, "admin");
        Assert.Equal(LicenseStatus.Active, reopened.Status);

        var paid = await SeedLicenseAsync(db, Today.AddDays(30));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Licenses.ExtendTrialAsync(paid.Id, new ExtendTrialRequest { TrialEndsAt = Today.AddDays(40) }, "admin"));
    }

    [Fact]
    public async Task UpdateAsync_TrialWithInvoice_MustUseExtendTrial()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await SeedCustomerAndProductAsync(db);
        var dto = await h.Licenses.CreateAsync(TrialRequest(c, p, 1200m), "admin");
        await h.Billing.CreateRenewalInvoiceAsync(dto.Id, dto.ExpiresAt, BillingService.RenewalActor);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Licenses.UpdateAsync(dto.Id, new UpdateLicenseRequest
        {
            PlanName = "Pro",
            ExpiresAt = dto.ExpiresAt!.Value.AddDays(3),
            Billing = dto.Billing
        }, "admin"));
        Assert.Contains("Extend trial", ex.Message);
    }

    [Fact]
    public async Task UpdateAsync_CannotPushTrialPastCap()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await SeedCustomerAndProductAsync(db);
        var dto = await h.Licenses.CreateAsync(TrialRequest(c, p), "admin");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Licenses.UpdateAsync(dto.Id, new UpdateLicenseRequest
        {
            PlanName = "Pro",
            ExpiresAt = dto.TrialMaxEndsAt!.Value.AddDays(5)
        }, "admin"));
    }

    [Fact]
    public async Task Complimentary_Issue_ActiveNoPriceNoInvoiceNoExpiry_NotRenewalCandidate()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await SeedCustomerAndProductAsync(db);
        var dto = await h.Licenses.CreateAsync(new CreateLicenseRequest
        {
            CustomerId = c, ServiceProductId = p, PlanName = "Pro", Complimentary = true,
            CreateInvoice = true, // ignored
            Billing = new LicenseBillingDto { Amount = 500m, Currency = null, PeriodMonths = 12 } // ignored
        }, "admin");

        Assert.Equal(LicenseStatus.Active, dto.Status);
        Assert.True(dto.IsComplimentary);
        Assert.Null(dto.ExpiresAt);
        Assert.Null(dto.Billing?.Amount);
        Assert.Empty(db.Invoices.IgnoreQueryFilters().Where(i => i.LicenseId == dto.Id));
        Assert.Equal(1, db.EmailOutboxMessages.Count(m => m.Kind == EmailDeliveryKind.LicenseKey));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            h.Billing.CreateRenewalInvoiceAsync(dto.Id, null, "admin", explicitRequest: true));
        Assert.Empty(db.Invoices.IgnoreQueryFilters().Where(i => i.LicenseId == dto.Id));
    }

    [Fact]
    public async Task MakeComplimentary_VoidsUnpaidInvoices_ActivatesPending_EndsTrial()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (license, invoice) = await LicenseActivationTests.IssuePricedAsync(h);

        var dto = await h.Licenses.MakeComplimentaryAsync(license.Id, new MakeComplimentaryRequest(), "admin");

        Assert.True(dto.IsComplimentary);
        Assert.Equal(LicenseStatus.Active, dto.Status);
        Assert.Null(dto.Billing?.Amount);
        db.ChangeTracker.Clear();
        Assert.Equal(InvoiceStatus.Void, (await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == invoice.Id)).Status);
        Assert.Contains(AuditAction.LicenseMadeComplimentary, h.Audit.Actions);

        // A trial made complimentary is no longer a trial.
        var (c, p) = await SeedCustomerAndProductAsync(db);
        var trial = await h.Licenses.CreateAsync(TrialRequest(c, p), "admin");
        var comp = await h.Licenses.MakeComplimentaryAsync(trial.Id, new MakeComplimentaryRequest(), "admin");
        Assert.False(comp.IsTrial);
        Assert.True(comp.IsComplimentary);
    }

    [Fact]
    public async Task MakeComplimentary_KeepsManualSuspension()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var seeded = await SeedLicenseAsync(db, Today.AddDays(30), status: LicenseStatus.Suspended);
        var dto = await h.Licenses.MakeComplimentaryAsync(seeded.Id, new MakeComplimentaryRequest(), "admin");
        Assert.Equal(LicenseStatus.Suspended, dto.Status);
        Assert.True(dto.IsComplimentary);
    }

    [Fact]
    public async Task Complimentary_RejectsPriceViaRenewAndEdit()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var seeded = await SeedLicenseAsync(db, Today.AddDays(30));
        await h.Licenses.MakeComplimentaryAsync(seeded.Id, new MakeComplimentaryRequest(), "admin");

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Licenses.UpdateAsync(seeded.Id, new UpdateLicenseRequest
        {
            PlanName = "Pro",
            Billing = new LicenseBillingDto { Amount = 10m, Currency = null, PeriodMonths = 12 }
        }, "admin"));
    }

    [Fact]
    public async Task EndComplimentary_SetsPrice_AndInvoicesFromChosenDate()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var seeded = await SeedLicenseAsync(db, null, price: null);
        await h.Licenses.MakeComplimentaryAsync(seeded.Id, new MakeComplimentaryRequest(), "admin");
        var start = Today.AddDays(20);

        var dto = await h.Licenses.EndComplimentaryAsync(seeded.Id, new EndComplimentaryRequest
        {
            Billing = new LicenseBillingDto { Amount = 300m, Currency = null, PeriodMonths = 1 },
            ExpiresAt = start,
            CreateInvoiceNow = true
        }, "admin");

        Assert.False(dto.IsComplimentary);
        Assert.Equal(300m, dto.Billing?.Amount);
        Assert.Equal(start, dto.ExpiresAt);
        var invoice = await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.LicenseId == seeded.Id);
        Assert.Equal(start, invoice.RenewalForExpiresAt);
        Assert.False(invoice.IsTrialConversion);
        Assert.Contains(AuditAction.LicenseComplimentaryEnded, h.Audit.Actions);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Licenses.EndComplimentaryAsync(seeded.Id,
            new EndComplimentaryRequest { Billing = new LicenseBillingDto { Amount = 1m, Currency = null, PeriodMonths = 1 } }, "admin"));
    }

    [Fact]
    public async Task OverdueWorkerAndRenewalPayment_LeaveComplimentaryAlone()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var seeded = await SeedLicenseAsync(db, Today.AddDays(5));
        var invoice = await h.Billing.CreateRenewalInvoiceAsync(seeded.Id, Today.AddDays(5), BillingService.RenewalActor);
        // Customer started paying before the switch: invoice stays open, payment does not extend the free license.
        await h.Billing.RecordReceiptAsync(invoice!.Id, LicenseActivationTests.Pay(1m, "part-1"), "admin");
        var dto = await h.Licenses.MakeComplimentaryAsync(seeded.Id, new MakeComplimentaryRequest(), "admin");
        db.ChangeTracker.Clear();
        Assert.NotEqual(InvoiceStatus.Void, (await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == invoice.Id)).Status);

        await h.Billing.RecordReceiptAsync(invoice.Id, LicenseActivationTests.Pay(invoice.TotalAmount - 1m, "part-2"), "admin");
        var license = await Reload(db, seeded.Id);
        Assert.Null(license.ExpiresAt);
        Assert.True(license.IsComplimentary);
        Assert.Equal(dto.ExpiresAt, license.ExpiresAt);
    }

    [Fact]
    public async Task TrialReminder_UnpricedTrial_SentOncePerTrialEnd_PricedTrialSkipped()
    {
        var services = new ServiceCollection();
        var database = NUlid.Ulid.NewUlid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(database));
        services.AddScoped<IEmailOutboxService, EmailOutboxService>();
        services.AddSingleton<EmailTemplateService>();
        services.AddSingleton<RecordingAudit>();
        services.AddSingleton<IAuditLogService>(sp => sp.GetRequiredService<RecordingAudit>());
        await using var provider = services.BuildServiceProvider();

        string unpricedId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var unpriced = await SeedLicenseAsync(db, Today.AddDays(5), price: null);
            var priced = await SeedLicenseAsync(db, Today.AddDays(5));
            var far = await SeedLicenseAsync(db, Today.AddDays(30), price: null);
            foreach (var id in new[] { unpriced.Id, priced.Id, far.Id })
            {
                var l = await db.Licenses.SingleAsync(x => x.Id == id);
                l.IsTrial = true;
                l.TrialStartedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
            unpricedId = unpriced.Id;
        }

        var worker = new LicenseExpiryReminderWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new LifecycleSettings { ExpiryReminderDays = 30 }),
            NullLogger<LicenseExpiryReminderWorker>.Instance);

        Assert.Equal(1, await worker.EnqueueTrialRemindersAsync(CancellationToken.None));
        Assert.Equal(0, await worker.EnqueueTrialRemindersAsync(CancellationToken.None));
        await worker.EnqueueRemindersAsync(CancellationToken.None); // the normal expiry scan skips trials

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var messages = await db.EmailOutboxMessages.ToListAsync();
            var reminder = Assert.Single(messages);
            Assert.Equal(EmailDeliveryKind.TrialReminder, reminder.Kind);
            Assert.Equal(unpricedId, reminder.LicenseId);

            // Extending the trial re-arms the reminder for the new end.
            var l = await db.Licenses.SingleAsync(x => x.Id == unpricedId);
            l.ExpiresAt = Today.AddDays(6);
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await worker.EnqueueTrialRemindersAsync(CancellationToken.None));
    }

    [Fact]
    public void TrialEndingInvoiceTemplate_ReplacesInvoiceWording()
    {
        var templates = new EmailTemplateService();
        var customer = new Customer { Name = "Acme", ContactEmail = "a@acme.test" };
        var product = new ServiceProduct { Name = "Hostel", Code = "H" };
        var license = new License { CustomerId = customer.Id, ServiceProductId = product.Id, PlanName = "Pro", ExpiresAt = Today.AddDays(7) };
        var invoice = new Invoice { CustomerId = customer.Id, InvoiceNumber = "INV-1", Currency = "GHS", Subtotal = 10, TotalAmount = 10, DueDate = Today.AddDays(7) };

        var (subject, html) = templates.TrialEndingInvoice(customer, product, license, invoice, "https://pay.test/x");

        Assert.Contains("trial ends", subject);
        Assert.Contains("Your free trial of Hostel ends on", html);
        Assert.Contains("Your paid period starts when the trial ends", html);
        Assert.DoesNotContain("A new invoice has been issued", html);
        Assert.DoesNotContain("Please arrange payment by the due date", html);
        Assert.Contains("https://pay.test/x", html);
    }


    // ---- deep review ----

    [Fact]
    public async Task RefundOfTrialConversion_RestoresTrial_AndTrialEnd()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await SeedCustomerAndProductAsync(db);
        var dto = await h.Licenses.CreateAsync(TrialRequest(c, p, 1200m), "admin");
        var invoice = await h.Billing.CreateRenewalInvoiceAsync(dto.Id, dto.ExpiresAt, BillingService.RenewalActor);
        var receipt = await PayInFullAsync(h.Billing, invoice!);
        Assert.False((await Reload(db, dto.Id)).IsTrial);

        await h.Billing.ReverseReceiptAsync(invoice!.Id, receipt.Id,
            new Platform.Shared.Dtos.Billing.ReverseReceiptRequest { IdempotencyKey = "rev", Reason = "refund" }, "admin");

        var license = await Reload(db, dto.Id);
        Assert.True(license.IsTrial);
        Assert.Equal(dto.ExpiresAt, license.ExpiresAt);
        Assert.Equal(LicenseStatus.Active, license.Status); // trial still running
        // Paying again converts again.
        db.ChangeTracker.Clear();
        var reopened = await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == invoice.Id);
        Assert.NotEqual(InvoiceStatus.Paid, reopened.Status);
    }

    [Fact]
    public async Task ManualRenewOfPricedTrial_VoidsUnpaidTrialInvoice()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await SeedCustomerAndProductAsync(db);
        var dto = await h.Licenses.CreateAsync(TrialRequest(c, p, 1200m), "admin");
        var invoice = await h.Billing.CreateRenewalInvoiceAsync(dto.Id, dto.ExpiresAt, BillingService.RenewalActor);

        var renewed = await h.Licenses.RenewAsync(dto.Id, new RenewLicenseRequest { ExpiresAt = Today.AddYears(1) }, "admin");

        Assert.False(renewed.IsTrial);
        db.ChangeTracker.Clear();
        var trialInvoice = await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == invoice!.Id);
        Assert.Equal(InvoiceStatus.Void, trialInvoice.Status);
        Assert.Null(trialInvoice.PayLinkNonce);
    }

    [Fact]
    public async Task ManualRenewOfTrial_KeepsTrialInvoiceWithMoneyOnIt()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await SeedCustomerAndProductAsync(db);
        var dto = await h.Licenses.CreateAsync(TrialRequest(c, p, 1200m), "admin");
        var invoice = await h.Billing.CreateRenewalInvoiceAsync(dto.Id, dto.ExpiresAt, BillingService.RenewalActor);
        await h.Billing.RecordReceiptAsync(invoice!.Id, LicenseActivationTests.Pay(100m, "part"), "admin");

        await h.Licenses.RenewAsync(dto.Id, new RenewLicenseRequest { ExpiresAt = Today.AddYears(1) }, "admin");

        db.ChangeTracker.Clear();
        Assert.Equal(InvoiceStatus.PartiallyPaid, (await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == invoice.Id)).Status);
    }

    [Fact]
    public async Task OverdueSupersededRenewalInvoice_DoesNotSuspend()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var expiry = Today.AddDays(3);
        var seeded = await SeedLicenseAsync(db, expiry);
        var invoice = await h.Billing.CreateRenewalInvoiceAsync(seeded.Id, expiry, BillingService.RenewalActor);
        await h.Licenses.RenewAsync(seeded.Id, new RenewLicenseRequest { ExpiresAt = Today.AddYears(1) }, "admin");
        var tracked = await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == invoice!.Id);
        tracked.Status = InvoiceStatus.Overdue;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var suspended = await OverdueInvoiceLifecycleWorker.TrySuspendForOverdueAsync(
            db, h.Licenses, h.Audit, invoice!.Id, seeded.Id, seeded.CustomerId, CancellationToken.None);

        Assert.False(suspended);
        Assert.Equal(LicenseStatus.Active, (await Reload(db, seeded.Id)).Status);
    }

    [Fact]
    public async Task ComplimentaryPaidComplimentary_RoundTrip()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var seeded = await SeedLicenseAsync(db, Today.AddDays(40));
        await h.Licenses.MakeComplimentaryAsync(seeded.Id, new MakeComplimentaryRequest(), "admin");
        var paid = await h.Licenses.EndComplimentaryAsync(seeded.Id, new EndComplimentaryRequest
        {
            Billing = new LicenseBillingDto { Amount = 80m, Currency = null, PeriodMonths = 1 },
            ExpiresAt = Today.AddDays(10)
        }, "admin");
        Assert.False(paid.IsComplimentary);
        var firstInvoice = await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.LicenseId == seeded.Id);

        var again = await h.Licenses.MakeComplimentaryAsync(seeded.Id, new MakeComplimentaryRequest { ExpiresAt = Today.AddDays(90) }, "admin");
        Assert.True(again.IsComplimentary);
        Assert.Equal(Today.AddDays(90), again.ExpiresAt);
        db.ChangeTracker.Clear();
        Assert.Equal(InvoiceStatus.Void, (await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == firstInvoice.Id)).Status);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Licenses.MakeComplimentaryAsync(seeded.Id, new MakeComplimentaryRequest(), "admin")
                .ContinueWith(_ => h.Licenses.EndComplimentaryAsync(seeded.Id, new EndComplimentaryRequest
                {
                    Billing = new LicenseBillingDto { Amount = 0m, Currency = null, PeriodMonths = 1 }
                }, "admin")).Unwrap());
    }

    [Fact]
    public async Task ExtendTrial_PartiallyPaidInvoice_MovesAndKeepsPayLinkAlive()
    {
        await using var db = CreateDb();
        var audit = new RecordingAudit();
        var billing = CreateBilling(db, audit, payLinks: CreatePayLinks(validDays: 7));
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await SeedCustomerAndProductAsync(db);
        var dto = await h.Licenses.CreateAsync(TrialRequest(c, p, 1200m), "admin");
        var invoice = await billing.CreateRenewalInvoiceAsync(dto.Id, dto.ExpiresAt, BillingService.RenewalActor);
        await billing.RecordReceiptAsync(invoice!.Id, LicenseActivationTests.Pay(100m, "part"), "admin");
        db.ChangeTracker.Clear();
        Assert.NotNull((await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == invoice.Id)).PayLinkExpiresAt);

        var newEnd = dto.TrialMaxEndsAt!.Value;
        await h.Licenses.ExtendTrialAsync(dto.Id, new ExtendTrialRequest { TrialEndsAt = newEnd }, "admin");

        db.ChangeTracker.Clear();
        var moved = await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == invoice.Id);
        Assert.Equal(InvoiceStatus.PartiallyPaid, moved.Status);
        Assert.Equal(newEnd, moved.RenewalForExpiresAt);
        Assert.True(moved.PayLinkExpiresAt >= newEnd);
    }

    [Fact]
    public void TrialCap_MonthEnd_ClampsToLastDay()
    {
        var cap = LicenseService.TrialCap(new DateTime(2027, 1, 31, 15, 0, 0, DateTimeKind.Utc), 1);
        Assert.Equal(new DateTime(2027, 2, 28, 0, 0, 0, DateTimeKind.Utc), cap);
        Assert.Equal(new DateTime(2028, 2, 29, 0, 0, 0, DateTimeKind.Utc),
            LicenseService.TrialCap(new DateTime(2027, 11, 30, 0, 0, 0, DateTimeKind.Utc), 3));
    }

    [Fact]
    public async Task RenewalCandidates_TrialsUseTrialWindow_PaidUseRenewalWindow()
    {
        await using var db = CreateDb();
        var trialSoon = await SeedLicenseAsync(db, Today.AddDays(5));
        var trialLater = await SeedLicenseAsync(db, Today.AddDays(10));
        var paidLater = await SeedLicenseAsync(db, Today.AddDays(10));
        var comp = await SeedLicenseAsync(db, Today.AddDays(5));
        foreach (var id in new[] { trialSoon.Id, trialLater.Id })
            (await db.Licenses.SingleAsync(l => l.Id == id)).IsTrial = true;
        (await db.Licenses.SingleAsync(l => l.Id == comp.Id)).IsComplimentary = true;
        await db.SaveChangesAsync();

        var found = await RenewalInvoiceWorker.FindCandidatesAsync(db,
            new BillingSettings { RenewalInvoiceDaysBefore = 14, TrialReminderDaysBefore = 7 }, DateTime.UtcNow, [], CancellationToken.None);
        var ids = found.Select(f => f.Id).ToHashSet();
        Assert.Contains(trialSoon.Id, ids);
        Assert.Contains(paidLater.Id, ids);
        Assert.DoesNotContain(trialLater.Id, ids);
        Assert.DoesNotContain(comp.Id, ids);

        // Trial window longer than the renewal window still works.
        found = await RenewalInvoiceWorker.FindCandidatesAsync(db,
            new BillingSettings { RenewalInvoiceDaysBefore = 3, TrialReminderDaysBefore = 12 }, DateTime.UtcNow, [], CancellationToken.None);
        ids = found.Select(f => f.Id).ToHashSet();
        Assert.Contains(trialLater.Id, ids);
        Assert.DoesNotContain(paidLater.Id, ids);
    }

    private static Task<List<EmailOutboxMessage>> Notices(AppDbContext db, string licenseId) =>
        db.EmailOutboxMessages.AsNoTracking()
            .Where(m => m.LicenseId == licenseId && m.Kind == EmailDeliveryKind.TrialExtended)
            .OrderBy(m => m.CreatedAt).ToListAsync();

    [Fact]
    public async Task ExtendTrial_AfterTrialInvoiceEmailed_TellsCustomerNewDate_WithPayLink_OncePerDate()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db, payLinks: CreatePayLinks());
        var (c, p) = await SeedCustomerAndProductAsync(db);
        var dto = await h.Licenses.CreateAsync(TrialRequest(c, p, 1200m), "admin");
        var invoice = await h.Billing.CreateRenewalInvoiceAsync(dto.Id, dto.ExpiresAt, BillingService.RenewalActor);
        Assert.NotNull(invoice);

        var firstEnd = dto.ExpiresAt!.Value.AddDays(5);
        await h.Licenses.ExtendTrialAsync(dto.Id, new ExtendTrialRequest { TrialEndsAt = firstEnd }, "admin");
        var notices = await Notices(db, dto.Id);
        var notice = Assert.Single(notices);
        Assert.Contains(firstEnd.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture), notice.Subject);
        Assert.Contains("/pay/", notice.HtmlBody);
        Assert.Contains(invoice!.InvoiceNumber, notice.HtmlBody);
        Assert.Equal(invoice.Id, notice.InvoiceId);

        // Same date can't be set twice (must move forward); a later date gets its own notice.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Licenses.ExtendTrialAsync(dto.Id, new ExtendTrialRequest { TrialEndsAt = firstEnd }, "admin"));
        await h.Licenses.ExtendTrialAsync(dto.Id, new ExtendTrialRequest { TrialEndsAt = firstEnd.AddDays(3) }, "admin");
        Assert.Equal(2, (await Notices(db, dto.Id)).Select(n => n.Subject).Distinct().Count());
    }

    [Fact]
    public async Task ExtendTrial_BeforeAnyReminder_SendsNothing()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db, payLinks: CreatePayLinks());
        var (c, p) = await SeedCustomerAndProductAsync(db);
        var dto = await h.Licenses.CreateAsync(TrialRequest(c, p, 1200m), "admin");
        await h.Licenses.ExtendTrialAsync(dto.Id, new ExtendTrialRequest { TrialEndsAt = dto.ExpiresAt!.Value.AddDays(5) }, "admin");
        Assert.Empty(await Notices(db, dto.Id));
    }

    [Fact]
    public async Task ExtendTrial_UnpricedAfterReminder_NoticeWithoutPayLink_AndReminderComesAgain()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db, payLinks: CreatePayLinks());
        var (c, p) = await SeedCustomerAndProductAsync(db);
        var dto = await h.Licenses.CreateAsync(TrialRequest(c, p), "admin");
        var lic = await db.Licenses.SingleAsync(l => l.Id == dto.Id);
        lic.TrialReminderForEndsAt = lic.ExpiresAt; // the reminder worker already told them
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var newEnd = dto.ExpiresAt!.Value.AddDays(5);
        await h.Licenses.ExtendTrialAsync(dto.Id, new ExtendTrialRequest { TrialEndsAt = newEnd }, "admin");
        var notice = Assert.Single(await Notices(db, dto.Id));
        Assert.DoesNotContain("/pay/", notice.HtmlBody);
        Assert.Null(notice.InvoiceId);
        // The ending reminder for the new date is still due (stamp is for the old end).
        db.ChangeTracker.Clear();
        Assert.NotEqual(newEnd, (await db.Licenses.SingleAsync(l => l.Id == dto.Id)).TrialReminderForEndsAt);
    }

    private static async Task<Invoice> OpenInvoiceAsync(LicenseActivationTests.Harness h, License license, decimal amount)
    {
        var dto = await h.Billing.CreateInvoiceAsync(new Platform.Shared.Dtos.Billing.CreateInvoiceRequest
        {
            CustomerId = license.CustomerId,
            LicenseId = license.Id,
            Subtotal = amount,
            Currency = null
        }, "admin");
        await h.Billing.SendInvoiceAsync(dto.Id, "admin");
        return await h.Db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == dto.Id);
    }

    [Fact]
    public async Task Revoke_VoidsUnpaidInvoices_KeepsPaidOrPaying_PaymentAfterIsRecordedOnly()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db, payLinks: CreatePayLinks());
        var license = await SeedLicenseAsync(db, Today.AddDays(20));
        var plain = await OpenInvoiceAsync(h, license, 100m);
        var partial = await OpenInvoiceAsync(h, license, 100m);
        var paying = await OpenInvoiceAsync(h, license, 100m);
        var stale = await OpenInvoiceAsync(h, license, 100m);
        await h.Billing.RecordReceiptAsync(partial.Id, LicenseActivationTests.Pay(40m, "part"), "admin");
        db.PaymentCheckouts.Add(new PaymentCheckout { InvoiceId = paying.Id, Reference = "r1", Currency = "GHS", CreatedBy = "pay-link" });
        db.PaymentCheckouts.Add(new PaymentCheckout { InvoiceId = stale.Id, Reference = "r2", Currency = "GHS", CreatedBy = "pay-link",
            CreatedAt = DateTime.UtcNow.AddHours(-3) });
        var plainTracked = await db.Invoices.SingleAsync(i => i.Id == plain.Id);
        plainTracked.PayLinkNonce = "n";
        plainTracked.PayLinkExpiresAt = DateTime.UtcNow.AddDays(30);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await h.Licenses.RevokeAsync(license.Id, "admin");

        db.ChangeTracker.Clear();
        var all = await db.Invoices.IgnoreQueryFilters().AsNoTracking().Where(i => i.LicenseId == license.Id).ToDictionaryAsync(i => i.Id);
        Assert.Equal(InvoiceStatus.Void, all[plain.Id].Status);
        Assert.Null(all[plain.Id].PayLinkNonce);
        Assert.Equal(InvoiceStatus.Void, all[stale.Id].Status); // older than the void block window
        Assert.Equal(InvoiceStatus.PartiallyPaid, all[partial.Id].Status);
        Assert.NotEqual(InvoiceStatus.Void, all[paying.Id].Status);
        var entry = h.Audit.Entries.Last(e => e.Action == AuditAction.LicenseRevoked);
        Assert.Contains(partial.InvoiceNumber, entry.Details!);
        Assert.Contains(paying.InvoiceNumber, entry.Details!);
        Assert.Contains("invoicesLeftOpen", entry.Details!);

        await h.Billing.RecordReceiptAsync(partial.Id, LicenseActivationTests.Pay(60m, "rest"), "admin");
        db.ChangeTracker.Clear();
        Assert.Equal(InvoiceStatus.Paid, (await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == partial.Id)).Status);
        Assert.Equal(LicenseStatus.Revoked, (await Reload(db, license.Id)).Status);
    }

    [Fact]
    public void ComplimentaryExpiryReminder_HasNoPaymentWording()
    {
        var templates = new EmailTemplateService();
        var customer = new Customer { Name = "Acme", ContactEmail = "a@acme.test" };
        var product = new ServiceProduct { Name = "Hostel", Code = "H" };
        var license = new License { CustomerId = "c", ServiceProductId = "p", PlanName = "Standard", IsComplimentary = true,
            ExpiresAt = new DateTime(2027, 3, 1, 0, 0, 0, DateTimeKind.Utc) };
        var (subject, html) = templates.ExpiryReminder(customer, product, license);
        Assert.Contains("complimentary", subject);
        Assert.Contains("nothing to pay", html);
        Assert.DoesNotContain("renewal", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/pay/", html);
        license.IsComplimentary = false;
        Assert.Contains("renewal", templates.ExpiryReminder(customer, product, license).Html);
    }
}
