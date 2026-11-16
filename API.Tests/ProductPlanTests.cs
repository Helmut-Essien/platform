using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Dtos.ServiceProducts;
using Platform.Shared.Enums;
using Xunit;
using static API.Tests.RenewalTests;

namespace API.Tests;

/// <summary>Per-product plan catalog: CRUD, license create/edit check, mapping of old names, validate contract (in-memory).</summary>
public class ProductPlanTests
{
    internal static ProductPlanService Plans(Platform.Api.Data.AppDbContext db) =>
        new(db, new AuditLogService(db));

    internal static async Task<ProductPlanDto> AddPlanAsync(
        Platform.Api.Data.AppDbContext db, string productId, string code, string? name = null, bool active = true, int order = 0) =>
        await Plans(db).CreateAsync(productId,
            new CreateProductPlanRequest { Code = code, DisplayName = name ?? code, IsActive = active, SortOrder = order }, "admin");

    internal static async Task<string> NewCustomerAsync(Platform.Api.Data.AppDbContext db, string name = "Other Ltd")
    {
        var c = new Customer { Name = name, ContactEmail = $"x+{NUlid.Ulid.NewUlid()}@acme.test" };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return c.Id;
    }

    internal static async Task<string> NewProductAsync(Platform.Api.Data.AppDbContext db, string code)
    {
        var p = new ServiceProduct { Name = code, Code = code, IsAvailableForSale = true };
        db.ServiceProducts.Add(p);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return p.Id;
    }

    internal static CreateLicenseRequest Issue(string customerId, string productId, string plan) =>
        new() { CustomerId = customerId, ServiceProductId = productId, PlanName = plan };

    internal static async Task<License> SeedLicenseAsync(
        Platform.Api.Data.AppDbContext db, string productId, string plan, LicenseStatus status = LicenseStatus.Active,
        bool suspendedCustomer = false)
    {
        var c = new Customer { Name = "Seed", ContactEmail = $"s+{NUlid.Ulid.NewUlid()}@acme.test", IsSuspended = suspendedCustomer };
        var l = new License
        {
            Customer = c, CustomerId = c.Id, ServiceProductId = productId, PlanName = plan, Status = status,
            LicenseKeyLookupHash = NUlid.Ulid.NewUlid().ToString().PadRight(64, 'a')
        };
        db.AddRange(c, l);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return l;
    }

    // ----- catalog CRUD -------------------------------------------------------------------------------

    [Fact]
    public async Task CreatePlan_TrimsAndLowercasesCode_AuditsIt_AndListsInOrder()
    {
        await using var db = CreateDb();
        var (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);

        var workshop = await AddPlanAsync(db, p, "  Workshop ", "Workshop", order: 20);
        await AddPlanAsync(db, p, "starter", "Starter", order: 10);
        await AddPlanAsync(db, p, "production", "Production", order: 20);

        Assert.Equal("workshop", workshop.Code);
        var list = await Plans(db).ListAsync(p);
        Assert.Equal(["starter", "production", "workshop"], list.Select(x => x.Code)); // order, then code
        Assert.Equal(3, await db.AuditLogs.CountAsync(a => a.Action == AuditAction.ProductPlanCreated));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("work shop")]
    [InlineData("-starter")]
    [InlineData("starter-")]
    [InlineData("a--b")]
    [InlineData("pro!")]
    [InlineData("café")]
    [InlineData("\u212Aey")]   // Kelvin sign: culture lowering would fold it to "key"
    [InlineData("\u0130stanbul")] // dotted capital I
    [InlineData("a123456789a123456789a123456789a123456789a123456789x")] // 51 chars
    public async Task CreatePlan_RejectsInvalidCodes(string code)
    {
        await using var db = CreateDb();
        var (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => AddPlanAsync(db, p, code));
        Assert.Empty(db.ProductPlans);
    }

    [Theory]
    [InlineData("pro-annual")]
    [InlineData("pro_2")]
    [InlineData("a")]
    [InlineData("123")]
    public async Task CreatePlan_AcceptsSimpleCodes(string code)
    {
        await using var db = CreateDb();
        var (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        Assert.Equal(code, (await AddPlanAsync(db, p, code)).Code);
    }

    [Fact]
    public async Task CreatePlan_DuplicateCodeRejected_ButSameCodeOnAnotherProductAllowed()
    {
        await using var db = CreateDb();
        var (_, a) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        var b = await NewProductAsync(db, "OTHER");
        await AddPlanAsync(db, a, "starter");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AddPlanAsync(db, a, "STARTER", "Again"));
        Assert.Contains("already has a plan", ex.Message);
        Assert.Equal("starter", (await AddPlanAsync(db, b, "starter")).Code);
    }

    [Fact]
    public async Task CreatePlan_UnknownProduct_IsNotFound()
    {
        await using var db = CreateDb();
        await Assert.ThrowsAsync<NotFoundException>(() => AddPlanAsync(db, "nope", "starter"));
        await Assert.ThrowsAsync<NotFoundException>(() => Plans(db).ListAsync("nope"));
    }

    [Fact]
    public async Task UpdatePlan_EditsNameOrderActive_CodeStaysStable_OtherProductsPlanIsNotFound()
    {
        await using var db = CreateDb();
        var (_, a) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        var b = await NewProductAsync(db, "OTHER");
        var plan = await AddPlanAsync(db, a, "workshop", "Workshop");

        var updated = await Plans(db).UpdateAsync(a, plan.Id,
            new UpdateProductPlanRequest { DisplayName = "  Workshop Plus ", SortOrder = 5, IsActive = false }, "admin");

        Assert.Equal("workshop", updated.Code);
        Assert.Equal("Workshop Plus", updated.DisplayName);
        Assert.Equal(5, updated.SortOrder);
        Assert.False(updated.IsActive);
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.Action == AuditAction.ProductPlanUpdated));
        await Assert.ThrowsAsync<NotFoundException>(() => Plans(db).UpdateAsync(b, plan.Id,
            new UpdateProductPlanRequest { DisplayName = "x" }, "admin"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Plans(db).UpdateAsync(a, plan.Id,
            new UpdateProductPlanRequest { DisplayName = "   " }, "admin"));
    }

    [Fact]
    public async Task DeletePlan_BlockedWhileALicenseIsOnIt_AllowedOtherwise()
    {
        await using var db = CreateDb();
        var (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        var used = await AddPlanAsync(db, p, "workshop");
        var spare = await AddPlanAsync(db, p, "spare");
        await SeedLicenseAsync(db, p, "Workshop", LicenseStatus.Revoked); // even a revoked key counts (history)

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Plans(db).DeleteAsync(p, used.Id, "admin"));
        Assert.Contains("Deactivate", ex.Message);

        await Plans(db).DeleteAsync(p, spare.Id, "admin");
        Assert.Equal(["workshop"], (await Plans(db).ListAsync(p)).Select(x => x.Code));
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AuditAction.ProductPlanDeleted));
    }

    // ----- license create ------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ProductWithoutPlans_StillAcceptsFreeText_Trimmed_CaseKept()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);

        var dto = await h.Licenses.CreateAsync(Issue(c, p, "  Pro Annual "), "admin");

        Assert.Equal("Pro Annual", dto.PlanName);
    }

    [Fact]
    public async Task Create_ProductWithPlans_TrimsAndLowercasesToTheCode_AndAuditsTheCode()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        await AddPlanAsync(db, p, "workshop");

        var dto = await h.Licenses.CreateAsync(Issue(c, p, "  WorkShop "), "admin");

        Assert.Equal("workshop", dto.PlanName);
        Assert.Contains(h.Audit.Entries, e => e.Action == AuditAction.LicenseIssued && e.Details!.Contains("\"workshop\""));
    }

    [Fact]
    public async Task Create_ProductWithPlans_RejectsUnknownAndInactive_AndSavesNothing()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        await AddPlanAsync(db, p, "starter");
        await AddPlanAsync(db, p, "workshop");
        await AddPlanAsync(db, p, "retired", active: false);

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Licenses.CreateAsync(Issue(c, p, "Pro"), "admin"));
        Assert.Contains("'Pro' is not an active plan", unknown.Message);
        Assert.Contains("starter, workshop", unknown.Message);
        Assert.DoesNotContain("retired", unknown.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Licenses.CreateAsync(Issue(c, p, "retired"), "admin"));

        Assert.Empty(db.Licenses.IgnoreQueryFilters());
    }

    [Theory]
    [InlineData("\u212Aey")]
    [InlineData("\u0130stanbul")]
    public async Task Create_ProductWithPlans_DoesNotFoldLookAlikeLettersOntoACode(string typed)
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        await AddPlanAsync(db, p, "key");
        await AddPlanAsync(db, p, "istanbul");

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Licenses.CreateAsync(Issue(c, p, typed), "admin"));
    }

    [Fact]
    public async Task Create_ProductWhosePlansAreAllInactive_IsRejected_WithAClearMessage()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        await AddPlanAsync(db, p, "starter", active: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Licenses.CreateAsync(Issue(c, p, "starter"), "admin"));
        Assert.Contains("no active plans", ex.Message);
    }

    [Fact]
    public async Task Create_AnotherProductsPlanDoesNotCount()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, a) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        var b = await NewProductAsync(db, "OTHER");
        await AddPlanAsync(db, a, "starter");
        await AddPlanAsync(db, b, "pro");

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Licenses.CreateAsync(Issue(c, a, "pro"), "admin"));
        Assert.Equal("pro", (await h.Licenses.CreateAsync(Issue(c, b, "PRO"), "admin")).PlanName);
    }

    [Fact]
    public async Task Create_TrialAndComplimentary_AreCheckedToo()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        await AddPlanAsync(db, p, "workshop");

        var trial = TrialAndComplimentaryTests.TrialRequest(c, p); // plan "Pro"
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Licenses.CreateAsync(trial, "admin"));
        trial.PlanName = "Workshop";
        var dto = await h.Licenses.CreateAsync(trial, "admin");
        Assert.Equal("workshop", dto.PlanName);
        Assert.True(dto.IsTrial);
    }

    // ----- license edit --------------------------------------------------------------------------------

    private static UpdateLicenseRequest Edit(string plan, DateTime? expires = null) => new() { PlanName = plan, ExpiresAt = expires };

    [Fact]
    public async Task Update_ProductWithPlans_NormalizesToCode_AndRejectsUnknownOrInactive()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        await AddPlanAsync(db, p, "starter");
        await AddPlanAsync(db, p, "workshop");
        await AddPlanAsync(db, p, "retired", active: false);
        var dto = await h.Licenses.CreateAsync(Issue(c, p, "starter"), "admin");

        Assert.Equal("workshop", (await h.Licenses.UpdateAsync(dto.Id, Edit(" WORKSHOP "), "admin")).PlanName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Licenses.UpdateAsync(dto.Id, Edit("gold"), "admin"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Licenses.UpdateAsync(dto.Id, Edit("retired"), "admin"));

        db.ChangeTracker.Clear();
        Assert.Equal("workshop", (await db.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == dto.Id)).PlanName);
    }

    [Fact]
    public async Task Update_LegacyPlanNameNotYetMapped_CanStillBeEdited_AndIsKeptUnchanged()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        var legacy = await SeedLicenseAsync(db, p, "Pro");
        await AddPlanAsync(db, p, "workshop"); // catalog switched on, "Pro" not mapped yet

        var newExpiry = DateTime.UtcNow.Date.AddMonths(6);
        var dto = await h.Licenses.UpdateAsync(legacy.Id, Edit("Pro", newExpiry), "admin");
        Assert.Equal("Pro", dto.PlanName);
        Assert.Equal(newExpiry.Date, dto.ExpiresAt!.Value.Date);
        // Same name with different case/spaces is "unchanged" too and keeps the stored value.
        Assert.Equal("Pro", (await h.Licenses.UpdateAsync(legacy.Id, Edit(" pro ", newExpiry), "admin")).PlanName);
        // But it cannot move to another name outside the catalog.
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Licenses.UpdateAsync(legacy.Id, Edit("Growth", newExpiry), "admin"));
        // It can move onto an active plan.
        Assert.Equal("workshop", (await h.Licenses.UpdateAsync(legacy.Id, Edit("workshop", newExpiry), "admin")).PlanName);
    }

    [Fact]
    public async Task Update_LicenseOnADeactivatedPlan_KeepsItWhenEditingOtherFields()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        var plan = await AddPlanAsync(db, p, "starter");
        await AddPlanAsync(db, p, "workshop");
        var dto = await h.Licenses.CreateAsync(Issue(c, p, "starter"), "admin");
        await Plans(db).UpdateAsync(p, plan.Id, new UpdateProductPlanRequest { DisplayName = "Starter", IsActive = false }, "admin");

        var after = await h.Licenses.UpdateAsync(dto.Id, new UpdateLicenseRequest { PlanName = "starter", MaxDevices = 5 }, "admin");

        Assert.Equal("starter", after.PlanName);
        Assert.Equal(5, after.MaxDevices);
        // ...but a different license can't be put onto the inactive plan.
        var other = await NewCustomerAsync(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Licenses.CreateAsync(Issue(other, p, "starter"), "admin"));
    }

    [Fact]
    public async Task Update_ProductWithoutPlans_StaysFreeText_TrimmedOnly()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        var legacy = await SeedLicenseAsync(db, p, "Pro");

        Assert.Equal("Anything Goes", (await h.Licenses.UpdateAsync(legacy.Id, Edit("  Anything Goes "), "admin")).PlanName);
    }

    // ----- per-product default MaxDevices ------------------------------------------------------------------

    [Fact]
    public async Task ProductDefaultMaxDevices_AppliesWhenNoneTyped_ExplicitWins_ZeroIsUnlimited()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        var c2 = await NewCustomerAsync(db, "B");
        var c3 = await NewCustomerAsync(db, "C");
        var product = await db.ServiceProducts.FindAsync(p);
        product!.DefaultMaxDevices = 1;
        await db.SaveChangesAsync();

        Assert.Equal(1, (await h.Licenses.CreateAsync(Issue(c, p, "x"), "admin")).MaxDevices);
        var explicitReq = Issue(c2, p, "x");
        explicitReq.MaxDevices = 4;
        Assert.Equal(4, (await h.Licenses.CreateAsync(explicitReq, "admin")).MaxDevices);
        product.DefaultMaxDevices = 0;
        await db.SaveChangesAsync();
        Assert.Null((await h.Licenses.CreateAsync(Issue(c3, p, "x"), "admin")).MaxDevices);
    }

    [Fact]
    public async Task ProductWithoutDefault_FallsBackToTheServerDefault()
    {
        await using var db = CreateDb();
        var h = LicenseActivationTests.Create(db);
        var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);

        Assert.Equal(new Platform.Api.Configuration.LicensingSettings().DefaultMaxDevices,
            (await h.Licenses.CreateAsync(Issue(c, p, "x"), "admin")).MaxDevices);
    }

    [Fact]
    public async Task ServiceProductService_RoundTripsDefaultMaxDevices_AndListsPlans()
    {
        await using var db = CreateDb();
        var svc = new ServiceProductService(db, new AuditLogService(db));
        var created = await svc.CreateAsync(new CreateServiceProductRequest { Name = "NeedleBase", Code = "needlebase", DefaultMaxDevices = 1 }, "admin");
        Assert.Equal(1, created.DefaultMaxDevices);
        Assert.Empty(created.Plans);

        await AddPlanAsync(db, created.Id, "workshop", "Workshop", order: 2);
        await AddPlanAsync(db, created.Id, "starter", "Starter", order: 1);

        var listed = (await svc.ListAsync()).Single();
        Assert.Equal(["starter", "workshop"], listed.Plans.Select(x => x.Code));
        Assert.Equal(1, listed.DefaultMaxDevices);
        Assert.Equal(2, (await svc.GetAsync(created.Id))!.Plans.Count);

        // Null = unchanged: an older client that omits the field must not wipe the default.
        var updated = await svc.UpdateAsync(created.Id, new UpdateServiceProductRequest { Name = "NeedleBase", DefaultMaxDevices = null }, "admin");
        Assert.Equal(1, updated.DefaultMaxDevices);
        Assert.Equal(2, updated.Plans.Count);
        var cleared = await svc.UpdateAsync(created.Id, new UpdateServiceProductRequest { Name = "NeedleBase", ClearDefaultMaxDevices = true }, "admin");
        Assert.Null(cleared.DefaultMaxDevices);
        // A value wins over the clear flag.
        var both = await svc.UpdateAsync(created.Id, new UpdateServiceProductRequest { Name = "NeedleBase", DefaultMaxDevices = 2, ClearDefaultMaxDevices = true }, "admin");
        Assert.Equal(2, both.DefaultMaxDevices);
        var clamped = await svc.UpdateAsync(created.Id, new UpdateServiceProductRequest { Name = "NeedleBase", DefaultMaxDevices = 5000 }, "admin");
        Assert.Equal(1000, clamped.DefaultMaxDevices);
    }

    // ----- usage + remap ---------------------------------------------------------------------------------

    [Fact]
    public async Task Usage_ListsDistinctNamesPerProduct_WithCounts_AndMatchFlag()
    {
        await using var db = CreateDb();
        var (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        var other = await NewProductAsync(db, "OTHER");
        await SeedLicenseAsync(db, p, "Pro");
        await SeedLicenseAsync(db, p, "Pro");
        await SeedLicenseAsync(db, p, "Pro", LicenseStatus.Revoked);
        await SeedLicenseAsync(db, p, "pro"); // different value: listed separately (case-sensitive)
        await SeedLicenseAsync(db, p, "workshop", suspendedCustomer: true); // suspended customers are included
        await SeedLicenseAsync(db, other, "Elsewhere");
        await AddPlanAsync(db, p, "workshop");

        var usage = await Plans(db).GetUsageAsync(p);

        Assert.Equal(["Pro", "pro", "workshop"], usage.Select(u => u.PlanName));
        var pro = usage.Single(u => u.PlanName == "Pro");
        Assert.Equal(3, pro.LicenseCount);
        Assert.Equal(2, pro.RemappableCount);
        Assert.False(pro.IsActivePlanCode);
        Assert.True(usage.Single(u => u.PlanName == "workshop").IsActivePlanCode);
    }

    [Fact]
    public async Task Remap_DryRun_ChangesNothing_AndReportsTheCounts()
    {
        await using var db = CreateDb();
        var (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        await SeedLicenseAsync(db, p, "Pro");
        await SeedLicenseAsync(db, p, "Pro", LicenseStatus.Revoked);
        await AddPlanAsync(db, p, "workshop");

        var r = await Plans(db).RemapAsync(p, new RemapProductPlanRequest { FromPlanName = "Pro", ToCode = "workshop", DryRun = true }, "admin");

        Assert.True(r.DryRun);
        Assert.Equal(1, r.UpdatedCount);
        Assert.Equal(1, r.SkippedRevokedCount);
        Assert.Equal(2, await db.Licenses.IgnoreQueryFilters().CountAsync(l => l.PlanName == "Pro"));
        Assert.Empty(db.AuditLogs.Where(a => a.Action != AuditAction.ProductPlanCreated));
    }

    [Fact]
    public async Task Remap_MovesNonRevokedLicenses_ToTheCode_Audited_VersionBumped()
    {
        await using var db = CreateDb();
        var (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        var other = await NewProductAsync(db, "OTHER");
        var a = await SeedLicenseAsync(db, p, "Pro");
        var b = await SeedLicenseAsync(db, p, "Pro", LicenseStatus.Suspended, suspendedCustomer: true);
        var revoked = await SeedLicenseAsync(db, p, "Pro", LicenseStatus.Revoked);
        var untouchedName = await SeedLicenseAsync(db, p, "Growth");
        var otherProduct = await SeedLicenseAsync(db, other, "Pro");
        await AddPlanAsync(db, p, "workshop");

        var r = await Plans(db).RemapAsync(p, new RemapProductPlanRequest { FromPlanName = "Pro", ToCode = " WORKSHOP " }, "admin@x", "1.2.3.4");

        Assert.False(r.DryRun);
        Assert.Equal(2, r.UpdatedCount);
        Assert.Equal(1, r.SkippedRevokedCount);
        Assert.Equal("workshop", r.ToCode);
        db.ChangeTracker.Clear();
        var rows = await db.Licenses.IgnoreQueryFilters().ToDictionaryAsync(l => l.Id);
        Assert.Equal("workshop", rows[a.Id].PlanName);
        Assert.Equal("workshop", rows[b.Id].PlanName);
        Assert.Equal("Pro", rows[revoked.Id].PlanName);
        Assert.Equal("Growth", rows[untouchedName.Id].PlanName);
        Assert.Equal("Pro", rows[otherProduct.Id].PlanName);
        Assert.Equal(a.Version + 1, rows[a.Id].Version);

        var updates = await db.AuditLogs.Where(x => x.Action == AuditAction.LicenseUpdated).ToListAsync();
        Assert.Equal(2, updates.Count);
        Assert.All(updates, u => Assert.Contains("plan_remap", u.DetailsJson));
        Assert.Contains(updates, u => u.LicenseId == a.Id && u.CustomerId == a.CustomerId && u.PerformedBy == "admin@x" && u.IpAddress == "1.2.3.4");
        var summary = await db.AuditLogs.SingleAsync(x => x.Action == AuditAction.LicensePlansRemapped);
        Assert.Contains("\"updated\":2", summary.DetailsJson);
    }

    [Fact]
    public async Task Remap_FromIsExact_NoGuessing()
    {
        await using var db = CreateDb();
        var (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        await SeedLicenseAsync(db, p, "Pro");
        await AddPlanAsync(db, p, "workshop");

        var r = await Plans(db).RemapAsync(p, new RemapProductPlanRequest { FromPlanName = "pro", ToCode = "workshop" }, "admin");

        Assert.Equal(0, r.UpdatedCount);
        Assert.Equal("Pro", (await db.Licenses.IgnoreQueryFilters().SingleAsync()).PlanName);
        Assert.Empty(db.AuditLogs.Where(a => a.Action != AuditAction.ProductPlanCreated));
    }

    [Fact]
    public async Task Remap_TargetMustExistOnThisProduct_AndBeActive()
    {
        await using var db = CreateDb();
        var (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        var other = await NewProductAsync(db, "OTHER");
        await SeedLicenseAsync(db, p, "Pro");
        await AddPlanAsync(db, other, "workshop");
        await AddPlanAsync(db, p, "retired", active: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Plans(db).RemapAsync(p, new RemapProductPlanRequest { FromPlanName = "Pro", ToCode = "workshop" }, "admin"));
        var inactive = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Plans(db).RemapAsync(p, new RemapProductPlanRequest { FromPlanName = "Pro", ToCode = "retired" }, "admin"));
        Assert.Contains("inactive", inactive.Message);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Plans(db).RemapAsync("nope", new RemapProductPlanRequest { FromPlanName = "Pro", ToCode = "workshop" }, "admin"));
        Assert.Equal("Pro", (await db.Licenses.IgnoreQueryFilters().SingleAsync()).PlanName);
    }

    [Fact]
    public async Task Remap_FromEqualsTarget_IsANoOp()
    {
        await using var db = CreateDb();
        var (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        await SeedLicenseAsync(db, p, "workshop");
        await AddPlanAsync(db, p, "workshop");

        var r = await Plans(db).RemapAsync(p, new RemapProductPlanRequest { FromPlanName = "workshop", ToCode = "workshop" }, "admin");

        Assert.Equal(0, r.UpdatedCount);
        Assert.Empty(db.AuditLogs.Where(a => a.Action == AuditAction.LicensePlansRemapped));
    }

    [Fact]
    public async Task Remap_IsRepeatable_SecondRunFindsNothing()
    {
        await using var db = CreateDb();
        var (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        await SeedLicenseAsync(db, p, "Pro");
        await AddPlanAsync(db, p, "workshop");
        var req = new RemapProductPlanRequest { FromPlanName = "Pro", ToCode = "workshop" };

        Assert.Equal(1, (await Plans(db).RemapAsync(p, req, "admin")).UpdatedCount);
        Assert.Equal(0, (await Plans(db).RemapAsync(p, req, "admin")).UpdatedCount);
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AuditAction.LicensePlansRemapped));
    }

    [Fact]
    public async Task Remap_LeavesInvoicePlanNamesAlone()
    {
        await using var db = CreateDb();
        var (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        var license = await SeedLicenseAsync(db, p, "Pro");
        await AddPlanAsync(db, p, "workshop");
        var invoice = new Invoice
        {
            CustomerId = license.CustomerId, ServiceProductId = p, LicenseId = license.Id, InvoiceNumber = "INV-1",
            PlanName = "Pro", Currency = "GHS", DueDate = DateTime.UtcNow.AddDays(7), Status = InvoiceStatus.Sent
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        await Plans(db).RemapAsync(p, new RemapProductPlanRequest { FromPlanName = "Pro", ToCode = "workshop" }, "admin");

        db.ChangeTracker.Clear();
        Assert.Equal("Pro", (await db.Invoices.IgnoreQueryFilters().SingleAsync()).PlanName);
    }

    // ----- validate contract ----------------------------------------------------------------------------------

    [Fact]
    public async Task ValidateContract_Unchanged_AndCarriesTheMappedCodeInBodyAndToken()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        var f = await LicenseValidationServiceTests.SeedActiveLicenseAsync(db); // plan "Growth", product HOSTEL
        await AddPlanAsync(db, f.Product.Id, "growth", "Growth");
        await Plans(db).RemapAsync(f.Product.Id, new RemapProductPlanRequest { FromPlanName = "Growth", ToCode = "growth" }, "admin");

        var svc = LicenseValidationTestFactory.Create(db);
        var r = await svc.ValidateAsync("ik_test_integration_key_001",
            new ValidateLicenseRequest { LicenseKey = "HOSTEL-TEST-KEY1", ServiceCode = "HOSTEL" });

        Assert.True(r.IsValid);
        Assert.Equal("growth", r.PlanName);
        var payload = r.Token!.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        Assert.Equal("growth", json.RootElement.GetProperty("plan").GetString());
    }

    [Fact]
    public void ValidateContract_ShapeOfRequestAndResponse_IsFrozen()
    {
        static string[] Names(Type t) => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order().ToArray();

        Assert.Equal(
            ["ActiveDevices", "EmailMatches", "ExpiresAt", "IsValid", "LicenseId", "MaxDevices", "Message", "OfflineGraceUntil",
             "PlanName", "Reason", "RefreshAfter", "ServerTime", "Token"],
            Names(typeof(ValidateLicenseResponse)));
        Assert.DoesNotContain("Plans", Names(typeof(ValidateLicenseResponse)));
        Assert.DoesNotContain(Names(typeof(ValidateLicenseRequest)), n => n.Contains("Plan"));
    }
}
