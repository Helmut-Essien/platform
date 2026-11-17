using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;
using Xunit;
using static API.Tests.PlanPriceChangeTests;
using static API.Tests.RenewalTests;

namespace API.Tests;

/// <summary>Scheduled price changes on real PostgreSQL: migration, partial unique index, row locks, races. Skips without PLATFORM_TEST_DB.</summary>
public class PlanPriceChangePostgresTests : IAsyncLifetime
{
    private PostgresBehaviorTests.TempDatabase? _pg;

    public async Task InitializeAsync() => _pg = await PostgresBehaviorTests.TempDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    private static bool IsDeadlock(Exception ex) =>
        ex is PostgresException { SqlState: "40P01" } || (ex.InnerException is not null && IsDeadlock(ex.InnerException));

    [Fact]
    public async Task OnePendingChangePerLicense_IsEnforcedByTheDatabase()
    {
        if (_pg is null)
            return;
        await using var db = _pg.NewContext();
        var f = await SeedPlanAsync(db);
        var license = await SeedLicenseAsync(db, f.ProductId);
        LicensePriceChange Change(LicensePriceChangeStatus status) => new()
        {
            LicenseId = license.Id, BatchId = "b", PlanCode = "workshop", NewCurrency = "GHS", NewAmount = 1m,
            EffectiveFrom = Today, Status = status
        };
        db.LicensePriceChanges.AddRange(Change(LicensePriceChangeStatus.Pending), Change(LicensePriceChangeStatus.Cancelled),
            Change(LicensePriceChangeStatus.Applied));
        await db.SaveChangesAsync();

        db.LicensePriceChanges.Add(Change(LicensePriceChangeStatus.Pending));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("IX_LicensePriceChanges_OnePendingPerLicense", Platform.Api.Data.PostgresUniqueViolation.ConstraintName(ex));
    }

    [Fact]
    public async Task ScheduleReplaceAndRenewal_WorkOnPostgres()
    {
        if (_pg is null)
            return;
        var expiry = Today.AddDays(10);
        string productId, planId, a, b;
        await using (var seed = _pg.NewContext())
        {
            var f = await SeedPlanAsync(seed);
            (productId, planId) = (f.ProductId, f.Plan.Id);
            a = (await SeedLicenseAsync(seed, productId, expiresAt: expiry)).Id;
            b = (await SeedLicenseAsync(seed, productId, expiresAt: expiry)).Id;
        }

        await using (var db = _pg.NewContext())
            await PriceChanges(db).ScheduleAsync(productId, planId, Schedule(expiry, [a, b], amount: 700m), "admin");
        await using (var db = _pg.NewContext())
        {
            // Replacing in one transaction: the old row is cancelled before the new one is inserted.
            var result = await PriceChanges(db).ScheduleAsync(productId, planId, Schedule(expiry, [a, b], amount: 750m, updateDefault: true), "admin");
            Assert.Equal(2, result.ReplacedCount);
        }
        await using (var db = _pg.NewContext())
        {
            var invoice = await CreateBilling(db).CreateRenewalInvoiceAsync(a, expiry, BillingService.RenewalActor);
            Assert.Equal(750m, invoice!.Subtotal);
        }
        await using var check = _pg.NewContext();
        Assert.Equal(750m, (await check.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == a)).BillingAmount);
        Assert.Equal(600m, (await check.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == b)).BillingAmount);
        Assert.Equal(LicensePriceChangeStatus.Pending,
            (await check.LicensePriceChanges.IgnoreQueryFilters().SingleAsync(c => c.LicenseId == b && c.Status != LicensePriceChangeStatus.Cancelled)).Status);
        Assert.Equal(750m, (await check.ProductPlans.SingleAsync(p => p.Id == planId)).DefaultAmount);
    }

    [Fact]
    public async Task Preview_OnPostgres_OrdersAlreadyScheduledLast_AndFlagsIssuedInvoices()
    {
        if (_pg is null)
            return;
        var expiry = Today.AddDays(10);
        string productId, planId, a, b;
        await using (var seed = _pg.NewContext())
        {
            var f = await SeedPlanAsync(seed);
            (productId, planId) = (f.ProductId, f.Plan.Id);
            a = (await SeedLicenseAsync(seed, productId, expiresAt: expiry, customerName: "Alpha")).Id;
            b = (await SeedLicenseAsync(seed, productId, expiresAt: expiry, customerName: "Beta")).Id;
        }
        await using (var db = _pg.NewContext())
            await CreateBilling(db).CreateRenewalInvoiceAsync(b, expiry, BillingService.RenewalActor);
        foreach (var months in new int?[] { null, 12 })
        {
            await using (var db = _pg.NewContext())
                await PriceChanges(db).ScheduleAsync(productId, planId, Schedule(expiry, [a], months: months), "admin");
            await using var check = _pg.NewContext();
            var preview = await PriceChanges(check).PreviewAsync(productId, planId, Schedule(expiry, [], months: months));
            Assert.Equal([b, a], preview.Rows.Select(r => r.LicenseId).ToArray());
            Assert.True(preview.Rows[0].NextInvoiceAlreadyIssued);
            Assert.Equal("Already scheduled at this price and date", preview.Rows[1].IneligibleReason);
        }
    }

    [Fact]
    public async Task ScheduleEditsRenewalsAndCancels_AllAtOnce_NeverDeadlock_AndStayConsistent()
    {
        if (_pg is null)
            return;
        var expiry = Today.AddDays(10);
        string productId, planId;
        string[] ids;
        await using (var seed = _pg.NewContext())
        {
            var f = await SeedPlanAsync(seed);
            (productId, planId) = (f.ProductId, f.Plan.Id);
            var list = new List<string>();
            for (var i = 0; i < 6; i++)
                list.Add((await SeedLicenseAsync(seed, productId, expiresAt: expiry)).Id);
            ids = [.. list];
        }

        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        async Task Run(Func<Platform.Api.Data.AppDbContext, Task> work)
        {
            await using var db = _pg!.NewContext();
            try { await work(db); }
            catch (Exception ex) { errors.Add(ex); }
        }

        for (var round = 0; round < 4; round++)
        {
            var work = new List<Task>
            {
                Task.Run(() => Run(db => PriceChanges(db).ScheduleAsync(productId, planId, Schedule(expiry, ids, amount: 700m + round), "admin"))),
                Task.Run(() => Run(db => PriceChanges(db).ScheduleAsync(productId, planId,
                    Schedule(expiry, ids.Reverse(), amount: 800m + round, updateDefault: true), "admin")))
            };
            foreach (var id in ids.Take(3))
                work.Add(Task.Run(() => Run(db => LicenseDeviceReleaseTests.CreateLicenses(db).UpdateAsync(id, new UpdateLicenseRequest
                {
                    PlanName = "workshop",
                    Billing = new LicenseBillingDto { Amount = 650m + round, Currency = "GHS", PeriodMonths = 12 }
                }, "admin"))));
            foreach (var id in ids.Skip(3))
                work.Add(Task.Run(() => Run(db => CreateBilling(db).CreateRenewalInvoiceAsync(id, expiry, BillingService.RenewalActor))));
            work.Add(Task.Run(() => Run(db => PriceChanges(db).QueueDueNoticesAsync(DateTime.UtcNow))));

            var all = Task.WhenAll(work);
            Assert.Same(all, await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(30))));
            await all;
        }

        Assert.DoesNotContain(errors, IsDeadlock);
        // Only "lost the race, refresh" outcomes are acceptable.
        Assert.All(errors, ex => Assert.True(
            ex is DbUpdateConcurrencyException or InvalidOperationException or ConflictException,
            $"unexpected {ex.GetType().Name}: {ex.Message}"));

        await using var check = _pg.NewContext();
        foreach (var id in ids)
            Assert.True(await check.LicensePriceChanges.IgnoreQueryFilters()
                .CountAsync(c => c.LicenseId == id && c.Status == LicensePriceChangeStatus.Pending) <= 1);
        // Every applied change points at the invoice that billed it, with that price.
        var applied = await check.LicensePriceChanges.IgnoreQueryFilters()
            .Where(c => c.Status == LicensePriceChangeStatus.Applied).ToListAsync();
        foreach (var change in applied)
        {
            var invoice = await check.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == change.AppliedInvoiceId);
            Assert.Equal(change.NewAmount, invoice.Subtotal);
        }
    }

    [Fact]
    public async Task TwoNoticeRunsAtOnce_SendEachNoticeOnce()
    {
        if (_pg is null)
            return;
        string productId, planId;
        var ids = new List<string>();
        await using (var seed = _pg.NewContext())
        {
            var f = await SeedPlanAsync(seed);
            (productId, planId) = (f.ProductId, f.Plan.Id);
            for (var i = 0; i < 8; i++)
                ids.Add((await SeedLicenseAsync(seed, productId)).Id);
        }
        await using (var db = _pg.NewContext())
            await PriceChanges(db).ScheduleAsync(productId, planId, Schedule(Today.AddDays(60), ids), "admin");

        var later = DateTime.UtcNow.AddDays(40);
        var runs = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            await using var db = _pg.NewContext();
            return await PriceChanges(db).QueueDueNoticesAsync(later);
        })));
        // Runs that lost the race send nothing; a later run picks up anything left.
        await using (var db = _pg.NewContext())
            await PriceChanges(db).QueueDueNoticesAsync(later);

        await using var check = _pg.NewContext();
        foreach (var id in ids)
            Assert.Equal(1, await check.EmailOutboxMessages.CountAsync(m => m.LicenseId == id && m.Kind == EmailDeliveryKind.PriceChangeNotice));
        Assert.True(runs.Sum() <= ids.Count);
    }
}
