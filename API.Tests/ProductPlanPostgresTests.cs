using Microsoft.EntityFrameworkCore;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Dtos.ServiceProducts;
using Platform.Shared.Enums;
using Xunit;
using static API.Tests.ProductPlanTests;

namespace API.Tests;

/// <summary>Product plan catalog on real PostgreSQL: unique index, cascade, grouping/remap SQL. Skips without PLATFORM_TEST_DB.</summary>
public class ProductPlanPostgresTests : IAsyncLifetime
{
    private PostgresBehaviorTests.TempDatabase? _pg;

    public async Task InitializeAsync() => _pg = await PostgresBehaviorTests.TempDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    [Fact]
    public async Task UniqueIndex_RejectsSameCodeOnOneProduct_AllowsItOnAnother()
    {
        if (_pg is null)
            return;
        await using var db = _pg.NewContext();
        var (_, a) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        var b = await NewProductAsync(db, "OTHER");
        db.ProductPlans.Add(new ProductPlan { ServiceProductId = a, Code = "starter", DisplayName = "Starter" });
        db.ProductPlans.Add(new ProductPlan { ServiceProductId = b, Code = "starter", DisplayName = "Starter" });
        await db.SaveChangesAsync();

        db.ProductPlans.Add(new ProductPlan { ServiceProductId = a, Code = "starter", DisplayName = "Dup" });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("IX_ProductPlans_ServiceProductId_Code", Platform.Api.Data.PostgresUniqueViolation.ConstraintName(ex));
    }

    [Fact]
    public async Task ConcurrentCreateOfTheSameCode_ExactlyOneSucceeds_OthersGetTheDuplicateMessage()
    {
        if (_pg is null)
            return;
        string p;
        await using (var seed = _pg.NewContext())
            (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(seed);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var db = _pg.NewContext();
            try
            {
                await AddPlanAsync(db, p, "workshop");
                return "ok";
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
        }));

        Assert.Single(outcomes, o => o == "ok");
        Assert.All(outcomes.Where(o => o != "ok"), o => Assert.Contains("already has a plan", o));
        await using var check = _pg.NewContext();
        Assert.Equal(1, await check.ProductPlans.CountAsync(x => x.ServiceProductId == p));
    }

    [Fact]
    public async Task DeletingAProduct_RemovesItsPlans()
    {
        if (_pg is null)
            return;
        await using var db = _pg.NewContext();
        var products = new ServiceProductService(db, new AuditLogService(db));
        var created = await products.CreateAsync(new CreateServiceProductRequest { Name = "Tmp", Code = "TMP", DefaultMaxDevices = 1 }, "admin");
        await AddPlanAsync(db, created.Id, "starter");
        await AddPlanAsync(db, created.Id, "workshop");
        Assert.Equal(1, (await products.GetAsync(created.Id))!.DefaultMaxDevices);

        await products.DeleteAsync(created.Id, "admin");

        await using var check = _pg.NewContext();
        Assert.Equal(0, await check.ProductPlans.CountAsync());
    }

    [Fact]
    public async Task DeletePlan_OnPostgres_BlockedWhileInUse_CaseInsensitively()
    {
        if (_pg is null)
            return;
        await using var db = _pg.NewContext();
        var (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        var used = await AddPlanAsync(db, p, "workshop");
        var spare = await AddPlanAsync(db, p, "spare");
        await SeedLicenseAsync(db, p, "Workshop");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Plans(db).DeleteAsync(p, used.Id, "admin"));
        await Plans(db).DeleteAsync(p, spare.Id, "admin");

        await using var check = _pg.NewContext();
        Assert.Equal(["workshop"], await check.ProductPlans.Where(x => x.ServiceProductId == p).Select(x => x.Code).ToListAsync());
    }

    [Fact]
    public async Task LicenseCreateAndEdit_AgainstTheCatalog_OnPostgres()
    {
        if (_pg is null)
            return;
        await using var db = _pg.NewContext();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);

        // No plans yet: free text, exactly as before.
        var legacyCustomer = await NewCustomerAsync(db, "Legacy");
        var legacy = await h.Licenses.CreateAsync(Issue(legacyCustomer, p, "Pro"), "admin");
        Assert.Equal("Pro", legacy.PlanName);

        await AddPlanAsync(db, p, "starter");
        await AddPlanAsync(db, p, "workshop");

        var dto = await h.Licenses.CreateAsync(Issue(c, p, " Workshop"), "admin");
        Assert.Equal("workshop", dto.PlanName);
        var third = await NewCustomerAsync(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Licenses.CreateAsync(Issue(third, p, "Pro"), "admin"));

        // The pre-existing "Pro" key can still be edited while unmapped, and keeps its name.
        var edited = await h.Licenses.UpdateAsync(legacy.Id, new UpdateLicenseRequest { PlanName = "Pro", MaxDevices = 2 }, "admin");
        Assert.Equal("Pro", edited.PlanName);
        Assert.Equal(2, edited.MaxDevices);
    }

    [Fact]
    public async Task UsageAndRemap_TranslateToSql_IncludeSuspendedCustomers_SkipRevoked()
    {
        if (_pg is null)
            return;
        await using var db = _pg.NewContext();
        var (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        await SeedLicenseAsync(db, p, "Pro");
        await SeedLicenseAsync(db, p, "Pro", LicenseStatus.Suspended, suspendedCustomer: true);
        await SeedLicenseAsync(db, p, "Pro", LicenseStatus.Revoked);
        await SeedLicenseAsync(db, p, "Growth");
        await AddPlanAsync(db, p, "workshop");

        var usage = await Plans(db).GetUsageAsync(p);
        var pro = usage.Single(u => u.PlanName == "Pro");
        Assert.Equal((3, 2), (pro.LicenseCount, pro.RemappableCount));
        Assert.Equal("Growth", usage[1].PlanName);

        var dry = await Plans(db).RemapAsync(p, new RemapProductPlanRequest { FromPlanName = "Pro", ToCode = "workshop", DryRun = true }, "admin");
        Assert.Equal((2, 1), (dry.UpdatedCount, dry.SkippedRevokedCount));

        var done = await Plans(db).RemapAsync(p, new RemapProductPlanRequest { FromPlanName = "Pro", ToCode = "workshop" }, "admin");
        Assert.Equal(2, done.UpdatedCount);

        await using var check = _pg.NewContext();
        var names = await check.Licenses.IgnoreQueryFilters().Where(l => l.ServiceProductId == p)
            .GroupBy(l => l.PlanName).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        Assert.Equal(2, names["workshop"]);
        Assert.Equal(1, names["Pro"]); // the revoked one
        Assert.Equal(1, names["Growth"]);
        Assert.Equal(3, await check.AuditLogs.CountAsync(a => a.Action == AuditAction.LicenseUpdated || a.Action == AuditAction.LicensePlansRemapped));
    }

    [Fact]
    public async Task ConcurrentRemaps_SerializeOnTheRowLocks_NoConflicts_EachLicenseMovedAndAuditedOnce()
    {
        if (_pg is null)
            return;
        string p;
        await using (var seed = _pg.NewContext())
        {
            (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(seed);
            await AddPlanAsync(seed, p, "workshop");
            for (var i = 0; i < 8; i++)
                await SeedLicenseAsync(seed, p, "Pro");
        }

        var counts = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var db = _pg.NewContext();
            return (await Plans(db).RemapAsync(p, new RemapProductPlanRequest { FromPlanName = "Pro", ToCode = "workshop" }, "admin")).UpdatedCount;
        }));

        // Whoever got the locks first moved all 8; the others waited, found nothing left, and changed nothing.
        Assert.Equal(8, counts.Sum());
        await using var check = _pg.NewContext();
        Assert.Equal(8, await check.Licenses.IgnoreQueryFilters().CountAsync(l => l.ServiceProductId == p && l.PlanName == "workshop"));
        Assert.All(await check.Licenses.IgnoreQueryFilters().Where(l => l.ServiceProductId == p).ToListAsync(), l => Assert.Equal(1, l.Version));
        Assert.Equal(8, await check.AuditLogs.CountAsync(a => a.Action == AuditAction.LicenseUpdated));
        Assert.Equal(counts.Count(n => n > 0), await check.AuditLogs.CountAsync(a => a.Action == AuditAction.LicensePlansRemapped));
    }

    [Fact]
    public async Task Remap_WaitsForAnInFlightLicenseEdit_ThenBuildsOnItInsteadOfOverwritingIt()
    {
        if (_pg is null)
            return;
        string p;
        string id;
        await using (var seed = _pg.NewContext())
        {
            (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(seed);
            await AddPlanAsync(seed, p, "workshop");
            id = (await SeedLicenseAsync(seed, p, "Pro")).Id;
        }

        // An admin's edit is mid-write on the license (device limit 3 -> 7, version bumped, not yet committed).
        await using var editor = _pg.NewContext();
        await using var tx = await editor.Database.BeginTransactionAsync();
        await editor.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "Licenses" SET "MaxDevices" = 7, "Version" = "Version" + 1 WHERE "Id" = {id}""");

        var remap = Task.Run(async () =>
        {
            await using var db = _pg.NewContext();
            return await Plans(db).RemapAsync(p, new RemapProductPlanRequest { FromPlanName = "Pro", ToCode = "workshop" }, "admin");
        });
        await Task.Delay(750);
        Assert.False(remap.IsCompleted); // waiting on the row lock, not reading a stale copy

        await tx.CommitAsync();
        Assert.Equal(1, (await remap).UpdatedCount);

        await using var check = _pg.NewContext();
        var row = await check.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == id);
        Assert.Equal(("workshop", 7, 2), (row.PlanName, row.MaxDevices, row.Version)); // both changes survived
    }

    [Fact]
    public async Task DeletePlan_WaitsForALicenseBeingCreatedOnIt_ThenRefusesBecauseItIsInUse()
    {
        if (_pg is null)
            return;
        string p;
        string planId;
        await using (var seed = _pg.NewContext())
        {
            (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(seed);
            planId = (await AddPlanAsync(seed, p, "workshop")).Id;
        }

        // A license create holds the shared plan lock (as ProductPlanRules does) and is about to insert its row.
        await using var creator = _pg.NewContext();
        await using var tx = await creator.Database.BeginTransactionAsync();
        await creator.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "ProductPlans" WHERE "ServiceProductId" = {p} FOR SHARE""");

        var delete = Task.Run(async () =>
        {
            await using var db = _pg.NewContext();
            await Plans(db).DeleteAsync(p, planId, "admin");
        });
        await Task.Delay(750);
        Assert.False(delete.IsCompleted); // blocked behind the creator, not racing it

        await SeedLicenseAsync(creator, p, "workshop");
        await tx.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => delete);
        await using var check = _pg.NewContext();
        Assert.Equal(1, await check.ProductPlans.CountAsync(x => x.ServiceProductId == p));
    }

    [Fact]
    public async Task LicenseCreate_TakesTheSharedPlanLock_SoItWaitsForAPlanBeingDeleted()
    {
        if (_pg is null)
            return;
        string c, p;
        await using (var seed = _pg.NewContext())
        {
            (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(seed);
            await AddPlanAsync(seed, p, "workshop");
            await AddPlanAsync(seed, p, "starter");
        }

        // A plan delete holds the exclusive lock (as DeleteAsync does) and is about to remove "workshop".
        await using var deleter = _pg.NewContext();
        await using var tx = await deleter.Database.BeginTransactionAsync();
        await deleter.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "ProductPlans" WHERE "ServiceProductId" = {p} FOR UPDATE""");

        var create = Task.Run(async () =>
        {
            await using var db = _pg.NewContext();
            return await LicenseActivationTests.Create(db).Licenses.CreateAsync(Issue(c, p, "workshop"), "admin");
        });
        await Task.Delay(750);
        Assert.False(create.IsCompleted);

        await deleter.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "ProductPlans" WHERE "ServiceProductId" = {p} AND "Code" = 'workshop'""");
        await tx.CommitAsync();

        // The create re-reads the catalog after the delete committed: "workshop" is gone, so it is refused.
        await Assert.ThrowsAsync<InvalidOperationException>(() => create);
        await using var check = _pg.NewContext();
        Assert.Equal(0, await check.Licenses.IgnoreQueryFilters().CountAsync(l => l.ServiceProductId == p));
    }

    [Fact]
    public async Task LicenseEdit_TakesTheSharedPlanLock_SoItWaitsForAPlanBeingDeleted_ThenIsRefused()
    {
        if (_pg is null)
            return;
        string p, licenseId;
        await using (var seed = _pg.NewContext())
        {
            (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(seed);
            await AddPlanAsync(seed, p, "starter");
            await AddPlanAsync(seed, p, "workshop");
            licenseId = (await SeedLicenseAsync(seed, p, "starter")).Id;
        }

        // A plan delete holds the exclusive lock and removes "workshop".
        await using var deleter = _pg.NewContext();
        await using var tx = await deleter.Database.BeginTransactionAsync();
        await deleter.Database.ExecuteSqlInterpolatedAsync($"""SELECT 1 FROM "ProductPlans" WHERE "ServiceProductId" = {p} FOR UPDATE""");

        var edit = Task.Run(async () =>
        {
            await using var db = _pg.NewContext();
            return await LicenseActivationTests.Create(db).Licenses.UpdateAsync(licenseId, new UpdateLicenseRequest { PlanName = "workshop" }, "admin");
        });
        await Task.Delay(750);
        Assert.False(edit.IsCompleted); // waiting behind the delete, not racing it

        await deleter.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "ProductPlans" WHERE "ServiceProductId" = {p} AND "Code" = 'workshop'""");
        await tx.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => edit); // the plan is gone: refused, nothing saved
        await using var check = _pg.NewContext();
        Assert.Equal("starter", (await check.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == licenseId)).PlanName);
    }

    [Fact]
    public async Task LicenseEdit_And_PlanDelete_RacingManyTimes_NeverLeaveAKeyOnADeletedPlan()
    {
        if (_pg is null)
            return;
        for (var round = 0; round < 12; round++)
        {
            string p, licenseId, planId;
            await using (var seed = _pg.NewContext())
            {
                (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(seed);
                await AddPlanAsync(seed, p, "starter");
                planId = (await AddPlanAsync(seed, p, "workshop")).Id;
                licenseId = (await SeedLicenseAsync(seed, p, "starter")).Id;
            }

            var edit = Task.Run(async () =>
            {
                await using var db = _pg.NewContext();
                try { await LicenseActivationTests.Create(db).Licenses.UpdateAsync(licenseId, new UpdateLicenseRequest { PlanName = "workshop" }, "admin"); }
                catch (InvalidOperationException) { /* plan already gone */ }
            });
            var delete = Task.Run(async () =>
            {
                await using var db = _pg.NewContext();
                try { await Plans(db).DeleteAsync(p, planId, "admin"); }
                catch (InvalidOperationException) { /* the edit won: plan in use */ }
            });
            await Task.WhenAll(edit, delete);

            await using var check = _pg.NewContext();
            var onWorkshop = (await check.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == licenseId)).PlanName == "workshop";
            var planExists = await check.ProductPlans.AnyAsync(x => x.Id == planId);
            Assert.True(planExists || !onWorkshop, "a license ended up on a plan that no longer exists");
        }
    }

    [Fact]
    public async Task EditPlanDeleteAndRemap_AllAtOnce_FinishWithoutDeadlock()
    {
        if (_pg is null)
            return;
        string p, planId;
        string[] ids;
        await using (var seed = _pg.NewContext())
        {
            (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(seed);
            await AddPlanAsync(seed, p, "workshop");
            planId = (await AddPlanAsync(seed, p, "spare")).Id;
            var seeded = new List<string>();
            for (var i = 0; i < 6; i++)
                seeded.Add((await SeedLicenseAsync(seed, p, "Pro")).Id);
            ids = [.. seeded];
        }

        var work = new List<Task>();
        foreach (var id in ids.Take(3))
            work.Add(Task.Run(async () =>
            {
                await using var db = _pg.NewContext();
                try { await LicenseActivationTests.Create(db).Licenses.UpdateAsync(id, new UpdateLicenseRequest { PlanName = "Pro" }, "admin"); }
                catch (Exception ex) when (ex is DbUpdateConcurrencyException or InvalidOperationException) { }
            }));
        work.Add(Task.Run(async () =>
        {
            await using var db = _pg.NewContext();
            await Plans(db).RemapAsync(p, new RemapProductPlanRequest { FromPlanName = "Pro", ToCode = "workshop" }, "admin");
        }));
        work.Add(Task.Run(async () =>
        {
            await using var db = _pg.NewContext();
            await Plans(db).DeleteAsync(p, planId, "admin");
        }));

        var all = Task.WhenAll(work);
        Assert.Same(all, await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(30)))); // a deadlock would time out here
        await all;
    }
}
