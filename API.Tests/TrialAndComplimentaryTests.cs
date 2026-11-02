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
}
