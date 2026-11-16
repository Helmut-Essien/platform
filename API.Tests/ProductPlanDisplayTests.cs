using Microsoft.EntityFrameworkCore;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Dtos.ServiceProducts;
using Platform.Shared.Enums;
using Xunit;
using static API.Tests.LicenseActivationTests;
using static API.Tests.ProductPlanTests;
using static API.Tests.RenewalTests;

namespace API.Tests;

/// <summary>People read the plan's display name; the stored value, validate answer and token stay the code.</summary>
public class ProductPlanDisplayTests
{
    [Fact]
    public async Task LicenseDto_ShowsDisplayName_ForCodes_AndTheStoredNameForLegacyOrPlanlessProducts()
    {
        await using var db = CreateDb();
        var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        var plainProduct = await NewProductAsync(db, "PLAIN");
        await AddPlanAsync(db, p, "workshop", "Workshop Plan");
        await AddPlanAsync(db, p, "retired", "Retired Plan", active: false);
        var mapped = await SeedLicenseAsync(db, p, "workshop");
        var onInactive = await SeedLicenseAsync(db, p, "retired");
        var legacy = await SeedLicenseAsync(db, p, "Pro");
        var planless = await SeedLicenseAsync(db, plainProduct, "workshop"); // same text, another product: no plans there
        var h = Create(db);

        var page = await h.Licenses.ListAsync(pageSize: 50);
        string Shown(License l) => page.Items.Single(i => i.Id == l.Id).PlanDisplayName!;
        string Code(License l) => page.Items.Single(i => i.Id == l.Id).PlanName;

        Assert.Equal("Workshop Plan", Shown(mapped));
        Assert.Equal("Retired Plan", Shown(onInactive));
        Assert.Equal("Pro", Shown(legacy));
        Assert.Equal("workshop", Shown(planless));
        Assert.Equal("workshop", Code(mapped)); // the contract value is untouched

        var single = await h.Licenses.GetAsync(mapped.Id);
        Assert.Equal(("workshop", "Workshop Plan"), (single!.PlanName, single.PlanDisplayName));
    }

    [Fact]
    public async Task LicenseSearch_FindsADisplayName()
    {
        await using var db = CreateDb();
        var (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        await AddPlanAsync(db, p, "wk1", "Workshop Plus");
        var hit = await SeedLicenseAsync(db, p, "wk1");
        await SeedLicenseAsync(db, p, "Pro");

        var page = await Create(db).Licenses.ListAsync(search: "plus");

        Assert.Equal(hit.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task FirstInvoice_AndItsDescription_UseTheDisplayName()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        await AddPlanAsync(db, p, "workshop", "Workshop");

        var license = await h.Licenses.CreateAsync(new CreateLicenseRequest
        {
            CustomerId = c, ServiceProductId = p, PlanName = "workshop", CreateInvoice = true, InvoiceSubtotal = 100
        }, "admin");

        var invoice = await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.LicenseId == license.Id);
        Assert.Equal("Workshop", invoice.PlanName);
        Assert.Contains("License Workshop", invoice.Description);
        Assert.Equal("workshop", license.PlanName);
    }

    [Fact]
    public async Task ManualInvoice_ATypedCodeBecomesTheDisplayName_OtherTextIsKept()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        await AddPlanAsync(db, p, "workshop", "Workshop");

        async Task<string?> PlanOf(string? typed, bool withProduct = true) =>
            (await h.Billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                CustomerId = c, ServiceProductId = withProduct ? p : null, Subtotal = 10, PlanName = typed, Status = InvoiceStatus.Draft
            }, "admin")).PlanName;

        Assert.Equal("Workshop", await PlanOf(" workshop "));
        Assert.Equal("Custom thing", await PlanOf("Custom thing"));
        Assert.Equal("workshop", await PlanOf("workshop", withProduct: false)); // no product to look the code up in
        Assert.Null(await PlanOf(null));
    }

    [Fact]
    public async Task RenamingAPlan_ChangesNewInvoices_NeverOldOnes()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        var plan = await AddPlanAsync(db, p, "workshop", "Workshop");
        var license = await h.Licenses.CreateAsync(new CreateLicenseRequest
        {
            CustomerId = c, ServiceProductId = p, PlanName = "workshop", CreateInvoice = true, InvoiceSubtotal = 100
        }, "admin");

        await Plans(db).UpdateAsync(p, plan.Id, new UpdateProductPlanRequest { DisplayName = "Workshop Pro", SortOrder = 0, IsActive = true }, "admin");
        db.ChangeTracker.Clear();
        var second = await h.Billing.CreateInvoiceForLicenseAsync(
            await db.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == license.Id), 50, 0, "GHS", null, null, "admin");

        var all = await db.Invoices.IgnoreQueryFilters().Where(i => i.LicenseId == license.Id).OrderBy(i => i.CreatedAt).ToListAsync();
        Assert.Equal(["Workshop", "Workshop Pro"], all.Select(i => i.PlanName));
        Assert.Equal("Workshop Pro", second.PlanName);
    }

    [Fact]
    public async Task RenewalInvoice_UsesTheDisplayName_InPlanAndDescription()
    {
        await using var db = CreateDb();
        var expiry = DateTime.UtcNow.Date.AddDays(10);
        var license = await SeedLicenseAsync(db, expiry);
        await AddPlanAsync(db, license.ServiceProductId, "pro-annual", "Pro Annual");
        db.ChangeTracker.Clear();
        (await db.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == license.Id)).PlanName = "pro-annual";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var invoice = await Create(db).Billing.CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);

        Assert.Equal("Pro Annual", invoice!.PlanName);
        Assert.Contains("Pro Annual", invoice.Description);
        Assert.DoesNotContain("pro-annual", invoice.Description);
    }

    [Fact]
    public async Task LicenseKeyEmail_ShowsTheDisplayName_ButLegacyNamesAsStored()
    {
        await using var db = CreateDb();
        var h = Create(db);
        var (c, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        await AddPlanAsync(db, p, "workshop", "Workshop Plan");

        var license = await h.Licenses.CreateAsync(new CreateLicenseRequest { CustomerId = c, ServiceProductId = p, PlanName = "workshop", Trial = true }, "admin");

        var mail = await db.EmailOutboxMessages.SingleAsync(m => m.LicenseId == license.Id && m.Kind == EmailDeliveryKind.LicenseKey);
        Assert.Contains("Workshop Plan", mail.HtmlBody);
        Assert.DoesNotContain(">workshop<", mail.HtmlBody);

        // A legacy, unmapped name is shown exactly as stored.
        var other = await NewCustomerAsync(db, "Legacy Ltd");
        var legacyProduct = await NewProductAsync(db, "LEGACY");
        var legacy = await h.Licenses.CreateAsync(new CreateLicenseRequest { CustomerId = other, ServiceProductId = legacyProduct, PlanName = "Pro", Trial = true }, "admin");
        var legacyMail = await db.EmailOutboxMessages.SingleAsync(m => m.LicenseId == legacy.Id && m.Kind == EmailDeliveryKind.LicenseKey);
        Assert.Contains(">Pro<", legacyMail.HtmlBody);
    }

    [Fact]
    public async Task Lookup_IsExact_AndFallsBackToTheGivenName()
    {
        await using var db = CreateDb();
        var (_, p) = await TrialAndComplimentaryTests.SeedCustomerAndProductAsync(db);
        await AddPlanAsync(db, p, "workshop", "Workshop");

        Assert.Equal("Workshop", await ProductPlanDisplay.NameAsync(db, p, "workshop", default));
        Assert.Equal("WORKSHOP", await ProductPlanDisplay.NameAsync(db, p, "WORKSHOP", default)); // codes are lowercase; no guessing
        Assert.Equal("Pro", await ProductPlanDisplay.NameAsync(db, p, "Pro", default));
        Assert.Equal("", await ProductPlanDisplay.NameAsync(db, p, "", default));
        Assert.Equal("workshop", await ProductPlanDisplay.NameAsync(db, "no-such-product", "workshop", default));
    }
}
