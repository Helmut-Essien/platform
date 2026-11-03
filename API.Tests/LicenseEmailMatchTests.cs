using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Controllers;
using Platform.Api.Data;
using Platform.Api.Extensions;
using Platform.Api.Services;
using Platform.Api.Services.Licensing;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;
using Xunit;
using R = Platform.Shared.Dtos.Licenses.LicenseValidationReasons;

namespace API.Tests;

public class LicenseEmailMatchTests
{
    private const string Key = "HOSTEL-TEST-KEY1";
    private const string Ik = "ik_test_integration_key_001";

    private static Task<ValidateLicenseResponse> Validate(
        AppDbContext db, string? email, string? deviceId = null, LicenseEmailAuditThrottle? throttle = null,
        string key = Key, string? service = "HOSTEL")
    {
        var svc = LicenseValidationTestFactory.Create(db, emailAuditThrottle: throttle);
        return svc.ValidateAsync(Ik, new ValidateLicenseRequest { LicenseKey = key, ServiceCode = service, Email = email, DeviceId = deviceId });
    }

    [Theory]
    [InlineData("owner@acme.test")]
    [InlineData("  OWNER@Acme.TEST ")]
    [InlineData("\towner@acme.test\n")]
    public async Task ContactEmail_Variants_Match(string email)
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);

        var r = await Validate(db, email);

        Assert.True(r.IsValid);
        Assert.Equal(R.Ok, r.Reason);
        Assert.True(r.EmailMatches);
        Assert.NotNull(r.Token);
    }

    [Fact]
    public async Task BillingEmail_Matches_AndStoredWhitespaceIgnored()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        var f = await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);
        f.Customer.BillingEmail = " Accounts@Acme.test ";
        await db.SaveChangesAsync();

        Assert.True((await Validate(db, "accounts@acme.TEST")).EmailMatches);
        Assert.True((await Validate(db, "owner@acme.test")).EmailMatches);
    }

    [Fact]
    public async Task Omitted_Email_IsUnchanged()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);

        var r = await Validate(db, null);

        Assert.True(r.IsValid);
        Assert.Null(r.EmailMatches);
        Assert.NotNull(r.Token);
        Assert.Equal(0, await db.AuditLogs.CountAsync());
    }

    [Theory]
    [InlineData("someone@else.test")]
    [InlineData("owner@acme.tes")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Mismatch_NoToken_NoDeviceSlot_NoEmailLeak(string email)
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        var f = await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);
        f.License.MaxDevices = 1;
        await db.SaveChangesAsync();

        var r = await Validate(db, email, deviceId: "shop-1");

        Assert.False(r.IsValid);
        Assert.Equal(R.EmailMismatch, r.Reason);
        Assert.Null(r.Token);
        Assert.Null(r.LicenseId);
        Assert.Null(r.PlanName);
        Assert.Null(r.ExpiresAt);
        Assert.Null(r.MaxDevices);
        Assert.DoesNotContain("acme", r.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await db.LicenseDevices.IgnoreQueryFilters().CountAsync());

        // The slot is still free for the real owner.
        var ok = await Validate(db, "owner@acme.test", deviceId: "shop-1");
        Assert.True(ok.IsValid);
        Assert.Equal(1, ok.ActiveDevices);
        // One shop per key: a second shop id with the right email hits the device limit.
        var second = await Validate(db, "owner@acme.test", deviceId: "shop-2");
        Assert.Equal(R.DeviceLimit, second.Reason);
    }

    [Fact]
    public async Task Customer_WithoutEmails_Mismatches()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        var f = await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);
        f.Customer.ContactEmail = "";
        f.Customer.BillingEmail = null;
        await db.SaveChangesAsync();

        Assert.Equal(R.EmailMismatch, (await Validate(db, "owner@acme.test")).Reason);
        Assert.False(LicenseValidationService.EmailMatches("", "", null));
    }

    [Fact]
    public async Task Precedence_KeyAndServiceErrorsFirst_EmailBeforeStatus()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        var f = await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);

        Assert.Equal(R.InvalidKey, (await Validate(db, "x@y.test", key: "HOSTEL-WRONG-KEY1")).Reason);
        Assert.Equal(R.WrongService, (await Validate(db, "x@y.test", service: "OTHER")).Reason);

        f.License.Status = LicenseStatus.Suspended;
        await db.SaveChangesAsync();
        // Without the owner's email a key holder does not learn the license status.
        Assert.Equal(R.EmailMismatch, (await Validate(db, "x@y.test")).Reason);
        Assert.Equal(R.Suspended, (await Validate(db, "owner@acme.test")).Reason);
    }

    [Fact]
    public async Task TooLongEmail_InvalidRequest()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);

        var r = await Validate(db, new string('a', 243) + "@example.com"); // 255
        Assert.Equal(R.InvalidRequest, r.Reason);
        // Exactly 254 is accepted (and simply mismatches).
        Assert.Equal(R.EmailMismatch, (await Validate(db, new string('a', 242) + "@example.com")).Reason);
    }

    [Fact]
    public void TooLongEmail_ModelValidation_Returns400InvalidRequest()
    {
        var request = new ValidateLicenseRequest { LicenseKey = Key, Email = new string('a', 255) };
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        Assert.False(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            request, new System.ComponentModel.DataAnnotations.ValidationContext(request), results, true));

        var action = new ControllerActionDescriptor { ControllerTypeInfo = typeof(LicenseValidationController).GetTypeInfo() };
        var ctx = new ActionContext(new Microsoft.AspNetCore.Http.DefaultHttpContext(), new RouteData(), action);
        ctx.ModelState.AddModelError("Email", results[0].ErrorMessage!);
        var result = Assert.IsType<BadRequestObjectResult>(ControllerExtensions.InvalidModelState(ctx));
        var reason = result.Value!.GetType().GetProperty("reason")!.GetValue(result.Value);
        Assert.Equal(R.InvalidRequest, reason);
    }

    [Fact]
    public async Task MismatchAudit_IsThrottledPerLicense()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);
        var throttle = new LicenseEmailAuditThrottle();

        for (var i = 0; i < 20; i++)
            Assert.Equal(R.EmailMismatch, (await Validate(db, $"guess{i}@x.test", throttle: throttle)).Reason);

        var audits = await db.AuditLogs.Where(a => a.Action == AuditAction.LicenseEmailMismatch).ToListAsync();
        var audit = Assert.Single(audits);
        Assert.DoesNotContain("@", audit.DetailsJson ?? "");
    }

    [Fact]
    public void Throttle_ReopensAfterWindow_AndStaysBounded()
    {
        var t = new LicenseEmailAuditThrottle();
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var window = TimeSpan.FromMinutes(15);
        Assert.True(t.TryEnter("a", now, window));
        Assert.False(t.TryEnter("a", now.AddMinutes(14), window));
        Assert.True(t.TryEnter("a", now.AddMinutes(15), window));

        for (var i = 0; i < LicenseEmailAuditThrottle.MaxEntries + 50; i++)
            t.TryEnter("l" + i, now.AddMinutes(15), window);
        Assert.True(t.Count <= LicenseEmailAuditThrottle.MaxEntries);
    }

    [Theory]
    [InlineData("caf\u0065\u0301@example.com", "caf\u00e9@example.com")]   // NFD vs NFC
    [InlineData("ama@b\u00fccher.de", "ama@xn--bcher-kva.de")]           // IDN vs punycode
    [InlineData("ama@example.com.", "AMA@example.com")]                   // trailing root dot
    [InlineData("\u00a0ama@example.com\t", "ama@example.com")]           // non-breaking / tab whitespace
    public void EmailMatches_NormalizesUnicodeAndIdn(string typed, string stored)
    {
        Assert.True(LicenseValidationService.EmailMatches(typed, stored, null));
        Assert.True(LicenseValidationService.EmailMatches(typed, null, stored));
    }

    [Theory]
    [InlineData("\ud800@example.com")]
    [InlineData("@")]
    [InlineData("noat")]
    [InlineData("a@-bad-\u00fc-.de")]
    public void EmailMatches_OddInput_NeverThrows(string typed)
    {
        Assert.False(LicenseValidationService.EmailMatches(typed, "ama@example.com", "x@y.z"));
    }

    [Fact]
    public void Throttle_Release_ReopensWindow()
    {
        var t = new LicenseEmailAuditThrottle();
        var now = DateTime.UtcNow;
        Assert.True(t.TryEnter("L1", now, TimeSpan.FromMinutes(15)));
        Assert.False(t.TryEnter("L1", now, TimeSpan.FromMinutes(15)));
        t.Release("L1", now);
        Assert.True(t.TryEnter("L1", now, TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public async Task Throttle_IsRaceFree_OneWinnerPerWindow()
    {
        var t = new LicenseEmailAuditThrottle();
        var now = DateTime.UtcNow;
        var wins = 0;
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            if (t.TryEnter("L1", now, TimeSpan.FromMinutes(15))) Interlocked.Increment(ref wins);
        })));
        Assert.Equal(1, wins);
    }
}
