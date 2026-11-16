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
    public async Task ConcurrentRemaps_NeverCorrupt_AtMostRetryableConflicts()
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

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var db = _pg.NewContext();
            try
            {
                return (await Plans(db).RemapAsync(p, new RemapProductPlanRequest { FromPlanName = "Pro", ToCode = "workshop" }, "admin")).UpdatedCount;
            }
            catch (DbUpdateConcurrencyException)
            {
                return -1; // surfaced as a 409 "refresh and try again"
            }
        }));

        Assert.Contains(outcomes, n => n > 0);
        await using var check = _pg.NewContext();
        Assert.Equal(8, await check.Licenses.IgnoreQueryFilters().CountAsync(l => l.ServiceProductId == p && l.PlanName == "workshop"));
        // Every license is bumped exactly once overall (a losing writer rolls back entirely).
        Assert.All(await check.Licenses.IgnoreQueryFilters().Where(l => l.ServiceProductId == p).ToListAsync(), l => Assert.Equal(1, l.Version));
    }
}
