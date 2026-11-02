using Microsoft.EntityFrameworkCore;
using Platform.Api.Configuration;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Api.Services.Renewals;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;
using Xunit;
using static API.Tests.RenewalTests;

namespace API.Tests;

/// <summary>Trials/complimentary on real PostgreSQL: one-trial index, locking, conversion. Skips without PLATFORM_TEST_DB.</summary>
public class TrialPostgresTests : IAsyncLifetime
{
    private PostgresBehaviorTests.TempDatabase? _pg;

    public async Task InitializeAsync() => _pg = await PostgresBehaviorTests.TempDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    private static DateTime Today => DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);

    [Fact]
    public async Task OneTrialIndex_RejectsSecondTrialRow_EvenWhenFirstRevoked()
    {
        if (_pg is null)
            return;
        await using var db = _pg.NewContext();
        var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        db.Licenses.Add(new License
        {
            CustomerId = c, ServiceProductId = p, PlanName = "Pro", Status = LicenseStatus.Revoked,
            IsTrial = false, TrialStartedAt = DateTime.UtcNow.AddMonths(-2)
        });
        await db.SaveChangesAsync();
        db.Licenses.Add(new License
        {
            CustomerId = c, ServiceProductId = p, PlanName = "Pro", Status = LicenseStatus.Active,
            IsTrial = true, TrialStartedAt = DateTime.UtcNow
        });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("IX_Licenses_OneTrialPerCustomerService", Platform.Api.Data.PostgresUniqueViolation.ConstraintName(ex));
    }

    [Fact]
    public async Task ConcurrentTrialIssues_ExactlyOneSucceeds_WithTrialMessage()
    {
        if (_pg is null)
            return;
        string c, p;
        await using (var seed = _pg.NewContext())
            (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(seed);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            await using var db = _pg.NewContext();
            var h = LicenseActivationTests.Create(db);
            try
            {
                await h.Licenses.CreateAsync(TrialAndComplimentaryTests.TrialRequest(c, p), "admin");
                return "ok";
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
        }));

        Assert.Single(outcomes, o => o == "ok");
        await using var check = _pg.NewContext();
        Assert.Equal(1, await check.Licenses.IgnoreQueryFilters().CountAsync(l => l.CustomerId == c && l.ServiceProductId == p));
    }

    [Fact]
    public async Task PricedTrial_InvoicePaid_ConvertsOnce_UnderConcurrentPayments()
    {
        if (_pg is null)
            return;
        string licenseId;
        DateTime trialEnd;
        Platform.Shared.Dtos.Billing.InvoiceDto invoice;
        await using (var seed = _pg.NewContext())
        {
            var h = LicenseActivationTests.Create(seed);
            var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(seed);
            var dto = await h.Licenses.CreateAsync(TrialAndComplimentaryTests.TrialRequest(c, p, 1200m), "admin");
            licenseId = dto.Id;
            trialEnd = dto.ExpiresAt!.Value;
            invoice = (await h.Billing.CreateRenewalInvoiceAsync(licenseId, trialEnd, BillingService.RenewalActor))!;
        }

        await Task.WhenAll(Pay("a"), Pay("b"));

        await using var check = _pg.NewContext();
        var license = await check.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == licenseId);
        Assert.False(license.IsTrial);
        Assert.Equal(PeriodMath.AddMonths(trialEnd, 12), license.ExpiresAt);
        Assert.True((await check.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == invoice.Id)).IsTrialConversion);

        async Task Pay(string key)
        {
            await using var db = _pg!.NewContext();
            try
            {
                await PayInFullAsync(CreateBilling(db), invoice, key);
            }
            catch (InvalidOperationException)
            {
                // second full payment refused as overpayment
            }
        }
    }

    [Fact]
    public async Task ExtendTrial_RacingRenewalWorker_InvoiceAlwaysMatchesTrialEnd()
    {
        if (_pg is null)
            return;
        for (var round = 0; round < 4; round++)
        {
            string licenseId;
            DateTime trialEnd;
            await using (var seed = _pg.NewContext())
            {
                var h = LicenseActivationTests.Create(seed);
                var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(seed);
                var dto = await h.Licenses.CreateAsync(TrialAndComplimentaryTests.TrialRequest(c, p, 1200m), "admin");
                licenseId = dto.Id;
                trialEnd = dto.ExpiresAt!.Value;
            }
            var newEnd = trialEnd.AddDays(10);

            await Task.WhenAll(
                Task.Run(async () =>
                {
                    await using var db = _pg.NewContext();
                    try { await CreateBilling(db).CreateRenewalInvoiceAsync(licenseId, trialEnd, BillingService.RenewalActor); }
                    catch (ConflictException) { }
                    catch (InvalidOperationException) { } // expiry moved under it
                }),
                Task.Run(async () =>
                {
                    await using var db = _pg.NewContext();
                    await LicenseActivationTests.Create(db).Licenses
                        .ExtendTrialAsync(licenseId, new ExtendTrialRequest { TrialEndsAt = newEnd }, "admin");
                }));

            await using var check = _pg.NewContext();
            var license = await check.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == licenseId);
            Assert.Equal(newEnd, license.ExpiresAt);
            var open = await check.Invoices.IgnoreQueryFilters()
                .Where(i => i.LicenseId == licenseId && i.Status != InvoiceStatus.Void).ToListAsync();
            Assert.True(open.Count <= 1);
            Assert.All(open, i => Assert.Equal(newEnd, i.RenewalForExpiresAt));
        }
    }

    [Fact]
    public async Task MakeComplimentary_And_EndComplimentary_OnPostgres()
    {
        if (_pg is null)
            return;
        await using var db = _pg.NewContext();
        var h = LicenseActivationTests.Create(db);
        var (license, invoice) = await LicenseActivationTests.IssuePricedAsync(h);
        var comp = await h.Licenses.MakeComplimentaryAsync(license.Id, new MakeComplimentaryRequest(), "admin");
        Assert.Equal(LicenseStatus.Active, comp.Status);
        db.ChangeTracker.Clear();
        Assert.Equal(InvoiceStatus.Void, (await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == invoice.Id)).Status);

        var paid = await h.Licenses.EndComplimentaryAsync(license.Id, new EndComplimentaryRequest
        {
            Billing = new LicenseBillingDto { Amount = 50m, Currency = null, PeriodMonths = 1 },
            ExpiresAt = Today.AddDays(3)
        }, "admin");
        Assert.False(paid.IsComplimentary);
        db.ChangeTracker.Clear();
        Assert.Single(await db.Invoices.IgnoreQueryFilters()
            .Where(i => i.LicenseId == license.Id && i.Status != InvoiceStatus.Void).ToListAsync());

        // Kind filter
        var trials = await h.Licenses.ListAsync(kind: LicenseKind.Complimentary);
        Assert.DoesNotContain(trials.Items, l => l.Id == license.Id);
        var paidList = await h.Licenses.ListAsync(kind: LicenseKind.Paid);
        Assert.Contains(paidList.Items, l => l.Id == license.Id);
    }

    [Fact]
    public async Task MixedTrialActions_Concurrently_NoDeadlock_ConsistentEnd()
    {
        if (_pg is null)
            return;
        for (var round = 0; round < 5; round++)
        {
            string licenseId;
            Platform.Shared.Dtos.Billing.InvoiceDto invoice;
            await using (var seed = _pg.NewContext())
            {
                var h = LicenseActivationTests.Create(seed);
                var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(seed);
                var dto = await h.Licenses.CreateAsync(TrialAndComplimentaryTests.TrialRequest(c, p, 1200m), "admin");
                licenseId = dto.Id;
                invoice = (await h.Billing.CreateRenewalInvoiceAsync(licenseId, dto.ExpiresAt, BillingService.RenewalActor))!;
            }

            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            async Task Run(Func<LicenseActivationTests.Harness, Task> action)
            {
                await using var db = _pg!.NewContext();
                try { await action(LicenseActivationTests.Create(db)); }
                catch (InvalidOperationException) { } // business refusal (state changed under it) is fine
                catch (ConflictException) { }
                catch (Exception ex) { errors.Add(ex); }
            }

            await Task.WhenAll(
                Run(h => PayInFullAsync(h.Billing, invoice, "pay-" + round)),
                Run(h => h.Licenses.RenewAsync(licenseId, new RenewLicenseRequest { ExpiresAt = Today.AddYears(2) }, "admin")),
                Run(h => h.Licenses.ExtendTrialAsync(licenseId, new ExtendTrialRequest { TrialEndsAt = invoice.DueDate!.Value.AddDays(5) }, "admin")),
                Run(h => h.Licenses.MakeComplimentaryAsync(licenseId, new MakeComplimentaryRequest(), "admin")));

            Assert.Empty(errors);
            await using var check = _pg.NewContext();
            var license = await check.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == licenseId);
            var inv = await check.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == invoice.Id);
            // Never left as a trial with nothing open, and a paid trial invoice always ended the trial.
            if (inv.Status == InvoiceStatus.Paid)
                Assert.False(license.IsTrial);
            if (license.IsTrial)
                Assert.Equal(license.ExpiresAt, inv.RenewalForExpiresAt);
            Assert.False(license.IsTrial && license.IsComplimentary);
        }
    }
}
