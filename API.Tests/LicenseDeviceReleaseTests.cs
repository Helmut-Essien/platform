using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Services;
using Platform.Api.Services.Email;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;
using Xunit;
using static API.Tests.RenewalTests;

namespace API.Tests;

/// <summary>Admin release of device slots: one, all, idempotency, re-registration, audit and concurrency.</summary>
public class LicenseDeviceReleaseTests
{
    private const string Ik = "ik_test_integration_key_001";

    private static ValidateLicenseRequest Req(string device) =>
        new() { LicenseKey = "HOSTEL-TEST-KEY1", ServiceCode = "HOSTEL", DeviceId = device, DeviceName = "pc " + device };

    /// <summary>LicenseService with a real audit writer so audit rows are persisted (the shared harness records only).</summary>
    internal static LicenseService CreateLicenses(AppDbContext db)
    {
        var settings = new BillingSettings();
        var outbox = new EmailOutboxService(db);
        var templates = new EmailTemplateService();
        var audit = new AuditLogService(db);
        var keyDelivery = new LicenseKeyDeliveryService(db, outbox, LicenseActivationTests.CreateProtector(), templates);
        var billing = new BillingService(db, audit, outbox, templates, new NoopDeny(),
            billingSettings: Options.Create(settings), keyDelivery: keyDelivery);
        return new LicenseService(db, billing, audit, keyDelivery, new NoopDeny(), outbox, templates, Options.Create(settings));
    }

    private static async Task<(string LicenseId, LicenseValidationService Validate)> SeedAsync(AppDbContext db, int? max)
    {
        var f = await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);
        f.License.MaxDevices = max;
        await db.SaveChangesAsync();
        return (f.License.Id, LicenseValidationTestFactory.Create(db));
    }

    [Fact]
    public async Task ReleaseAll_FreesEveryActiveDevice_AuditsEach_AndIsIdempotent()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        var (id, validate) = await SeedAsync(db, 3);
        foreach (var d in new[] { "a", "b", "c" })
            Assert.True((await validate.ValidateAsync(Ik, Req(d))).IsValid);
        Assert.Equal(LicenseValidationReasons.DeviceLimit, (await validate.ValidateAsync(Ik, Req("d"))).Reason);
        var licenses = CreateLicenses(db);

        Assert.Equal(3, await licenses.ReleaseAllDevicesAsync(id, "admin@x", "1.1.1.1"));
        Assert.Equal(0, await licenses.ReleaseAllDevicesAsync(id, "admin@x"));

        var audits = await db.AuditLogs.Where(a => a.Action == AuditAction.LicenseDeviceReleased).ToListAsync();
        Assert.Equal(3, audits.Count);
        Assert.All(audits, a =>
        {
            Assert.Equal(id, a.LicenseId);
            Assert.Equal("admin@x", a.PerformedBy);
            Assert.Contains("releasedAll", a.DetailsJson);
        });
        Assert.All(await licenses.ListDevicesAsync(id), d => Assert.False(d.IsActive));
        // The freed slots are usable at once, by new and by released devices alike.
        Assert.True((await validate.ValidateAsync(Ik, Req("d"))).IsValid);
        Assert.True((await validate.ValidateAsync(Ik, Req("a"))).IsValid);
    }

    [Fact]
    public async Task ReleaseAll_OnlyTouchesThatLicense_AndKnowsMissingLicenses()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        var (id, validate) = await SeedAsync(db, 5);
        Assert.True((await validate.ValidateAsync(Ik, Req("a"))).IsValid);
        // A second license with its own active device.
        var other = new Platform.Api.Entities.License
        {
            CustomerId = (await db.Customers.FirstAsync()).Id, ServiceProductId = (await db.ServiceProducts.FirstAsync()).Id,
            PlanName = "x", Status = LicenseStatus.Revoked
        };
        db.Licenses.Add(other);
        db.LicenseDevices.Add(new Platform.Api.Entities.LicenseDevice { LicenseId = other.Id, DeviceIdHash = new string('b', 64) });
        await db.SaveChangesAsync();
        var licenses = CreateLicenses(db);

        Assert.Equal(1, await licenses.ReleaseAllDevicesAsync(id, "admin"));

        Assert.Null((await db.LicenseDevices.SingleAsync(d => d.LicenseId == other.Id)).RevokedAt);
        await Assert.ThrowsAsync<NotFoundException>(() => licenses.ReleaseAllDevicesAsync("nope", "admin"));
        await Assert.ThrowsAsync<NotFoundException>(() => licenses.ReleaseDeviceAsync("nope", "x", "admin"));
        // A device id of another license is "not found" for this one.
        var foreign = await db.LicenseDevices.Where(d => d.LicenseId == other.Id).Select(d => d.Id).SingleAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => licenses.ReleaseDeviceAsync(id, foreign, "admin"));
        // A revoked license's devices can still be released (cleanup).
        Assert.Equal(1, await licenses.ReleaseAllDevicesAsync(other.Id, "admin"));
    }

    [Fact]
    public async Task ReleaseOne_ThenSameDeviceReturns_ReRegistersOnlyIfASlotIsFree()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        var (id, validate) = await SeedAsync(db, 1);
        var licenses = CreateLicenses(db);
        Assert.True((await validate.ValidateAsync(Ik, Req("old-server"))).IsValid);
        var row = Assert.Single(await licenses.ListDevicesAsync(id));

        await licenses.ReleaseDeviceAsync(id, row.Id, "admin");
        // Old server (still holding a token) comes back first and takes the slot again...
        var back = await validate.ValidateAsync(Ik, Req("old-server"));
        Assert.True(back.IsValid);
        Assert.Equal(1, back.ActiveDevices);
        // ...so the new server is refused until the admin releases again.
        Assert.Equal(LicenseValidationReasons.DeviceLimit, (await validate.ValidateAsync(Ik, Req("new-server"))).Reason);
        await licenses.ReleaseDeviceAsync(id, row.Id, "admin");
        Assert.True((await validate.ValidateAsync(Ik, Req("new-server"))).IsValid);
        Assert.Equal(LicenseValidationReasons.DeviceLimit, (await validate.ValidateAsync(Ik, Req("old-server"))).Reason);
        // Re-registering reuses the same device row (no duplicates).
        Assert.Equal(2, (await licenses.ListDevicesAsync(id)).Count);
        Assert.Equal(2, await db.AuditLogs.CountAsync(a => a.Action == AuditAction.LicenseDeviceReleased));
    }

    [Fact]
    public async Task ReleaseOne_AlreadyReleased_WritesNoSecondAuditRow()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        var (id, validate) = await SeedAsync(db, 2);
        Assert.True((await validate.ValidateAsync(Ik, Req("a"))).IsValid);
        var licenses = CreateLicenses(db);
        var row = Assert.Single(await licenses.ListDevicesAsync(id));

        var first = await licenses.ReleaseDeviceAsync(id, row.Id, "admin");
        var second = await licenses.ReleaseDeviceAsync(id, row.Id, "admin");

        Assert.Equal(first.RevokedAt, second.RevokedAt);
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AuditAction.LicenseDeviceReleased));
    }
}
