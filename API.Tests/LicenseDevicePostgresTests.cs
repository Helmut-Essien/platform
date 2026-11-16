using Microsoft.EntityFrameworkCore;
using Platform.Shared.Dtos.Licenses;
using Xunit;

namespace API.Tests;

/// <summary>Device activation under real row locks and the (LicenseId, DeviceIdHash) unique index. Skips without Postgres.</summary>
public class LicenseDevicePostgresTests : IAsyncLifetime
{
    private PostgresBehaviorTests.TempDatabase? _pg;

    public async Task InitializeAsync() => _pg = await PostgresBehaviorTests.TempDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentNewDevices_NeverExceedMaxDevices_AndSameDeviceRaceActivatesOnce()
    {
        if (_pg is null)
            return;
        string licenseId;
        await using (var seed = _pg.NewContext())
        {
            var f = await LicenseValidationServiceTests.SeedActiveLicenseAsync(seed);
            f.License.MaxDevices = 2;
            await seed.SaveChangesAsync();
            licenseId = f.License.Id;
        }

        var settings = LicenseValidationTestFactory.Settings();
        var signer = LicenseValidationTestFactory.Signer(settings);
        // 6 distinct devices plus 4 copies of one device, all at once.
        var devices = Enumerable.Range(0, 6).Select(i => "pc-" + i).Concat(Enumerable.Repeat("pc-same", 4)).ToList();
        var results = await Task.WhenAll(devices.Select(async d =>
        {
            await using var db = _pg.NewContext();
            await using var svc = LicenseValidationTestFactory.Create(db, settings, signer: signer);
            return await svc.ValidateAsync("ik_test_integration_key_001", new ValidateLicenseRequest
            {
                LicenseKey = "HOSTEL-TEST-KEY1", ServiceCode = "HOSTEL", DeviceId = d
            });
        }));

        Assert.All(results, r => Assert.True(r.IsValid || r.Reason == LicenseValidationReasons.DeviceLimit));
        await using var check = _pg.NewContext();
        var rows = await check.LicenseDevices.IgnoreQueryFilters().Where(x => x.LicenseId == licenseId).ToListAsync();
        Assert.Equal(2, rows.Count(x => x.RevokedAt == null));
        Assert.Equal(rows.Count, rows.Select(x => x.DeviceIdHash).Distinct().Count());
        Assert.Equal(2, await check.AuditLogs.CountAsync(a => a.LicenseId == licenseId
            && a.Action == Platform.Shared.Enums.AuditAction.LicenseDeviceActivated));
    }

    [Fact]
    public async Task EmailMatch_TranslatesOnPostgres_AndMismatchTakesNoSlot()
    {
        if (_pg is null)
            return;
        await using (var seed = _pg.NewContext())
        {
            var f = await LicenseValidationServiceTests.SeedActiveLicenseAsync(seed);
            f.License.MaxDevices = 1;
            f.Customer.BillingEmail = "Billing@Acme.test";
            await seed.SaveChangesAsync();
        }

        async Task<ValidateLicenseResponse> Run(string? email, string device)
        {
            await using var db = _pg.NewContext();
            await using var svc = LicenseValidationTestFactory.Create(db);
            return await svc.ValidateAsync("ik_test_integration_key_001", new ValidateLicenseRequest
            {
                LicenseKey = "HOSTEL-TEST-KEY1", ServiceCode = "HOSTEL", DeviceId = device, Email = email
            });
        }

        Assert.Equal(LicenseValidationReasons.EmailMismatch, (await Run("nope@x.test", "shop-a")).Reason);
        var ok = await Run(" billing@ACME.test", "shop-a");
        Assert.True(ok.IsValid);
        Assert.True(ok.EmailMatches);
        Assert.Equal(1, ok.ActiveDevices);
        Assert.True((await Run(null, "shop-a")).IsValid);
        await using var check = _pg.NewContext();
        Assert.Equal(1, await check.AuditLogs.CountAsync(a => a.Action == Platform.Shared.Enums.AuditAction.LicenseEmailMismatch));
    }

    [Fact]
    public async Task ConcurrentReleases_OfOneDevice_ReleaseOnceAndAuditOnce()
    {
        if (_pg is null)
            return;
        string licenseId, deviceRowId;
        await using (var seed = _pg.NewContext())
        {
            var f = await LicenseValidationServiceTests.SeedActiveLicenseAsync(seed);
            f.License.MaxDevices = 2;
            await seed.SaveChangesAsync();
            licenseId = f.License.Id;
            await using var svc = LicenseValidationTestFactory.Create(seed);
            await svc.ValidateAsync("ik_test_integration_key_001", new ValidateLicenseRequest { LicenseKey = "HOSTEL-TEST-KEY1", ServiceCode = "HOSTEL", DeviceId = "pc-1" });
            deviceRowId = (await seed.LicenseDevices.SingleAsync()).Id;
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = _pg.NewContext();
            await LicenseDeviceReleaseTests.CreateLicenses(db).ReleaseDeviceAsync(licenseId, deviceRowId, "admin");
        }));

        await using var check = _pg.NewContext();
        Assert.NotNull((await check.LicenseDevices.SingleAsync()).RevokedAt);
        Assert.Equal(1, await check.AuditLogs.CountAsync(a => a.Action == Platform.Shared.Enums.AuditAction.LicenseDeviceReleased));
    }

    [Fact]
    public async Task ReleaseAll_RacingWithNewDevices_NeverExceedsMax_AndEveryReleaseIsAudited()
    {
        if (_pg is null)
            return;
        string licenseId;
        await using (var seed = _pg.NewContext())
        {
            var f = await LicenseValidationServiceTests.SeedActiveLicenseAsync(seed);
            f.License.MaxDevices = 3;
            await seed.SaveChangesAsync();
            licenseId = f.License.Id;
            await using var svc = LicenseValidationTestFactory.Create(seed);
            foreach (var d in new[] { "a", "b", "c" })
                await svc.ValidateAsync("ik_test_integration_key_001", new ValidateLicenseRequest { LicenseKey = "HOSTEL-TEST-KEY1", ServiceCode = "HOSTEL", DeviceId = d });
        }

        var settings = LicenseValidationTestFactory.Settings();
        var signer = LicenseValidationTestFactory.Signer(settings);
        var releases = Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var db = _pg.NewContext();
            return await LicenseDeviceReleaseTests.CreateLicenses(db).ReleaseAllDevicesAsync(licenseId, "admin");
        });
        var activations = Enumerable.Range(0, 8).Select(async i =>
        {
            await using var db = _pg.NewContext();
            await using var svc = LicenseValidationTestFactory.Create(db, settings, signer: signer);
            var r = await svc.ValidateAsync("ik_test_integration_key_001", new ValidateLicenseRequest
            {
                LicenseKey = "HOSTEL-TEST-KEY1", ServiceCode = "HOSTEL", DeviceId = "new-" + i
            });
            return r.IsValid || r.Reason == LicenseValidationReasons.DeviceLimit;
        });
        var released = await Task.WhenAll(releases);
        Assert.All(await Task.WhenAll(activations), ok => Assert.True(ok));

        await using var check = _pg.NewContext();
        var rows = await check.LicenseDevices.Where(x => x.LicenseId == licenseId).ToListAsync();
        Assert.True(rows.Count(x => x.RevokedAt == null) <= 3);
        Assert.Equal(rows.Count, rows.Select(x => x.DeviceIdHash).Distinct().Count());
        // Each device row's release is audited exactly as many times as it was really released (never twice for one release).
        var audited = await check.AuditLogs.CountAsync(a => a.Action == Platform.Shared.Enums.AuditAction.LicenseDeviceReleased);
        Assert.Equal(released.Sum(), audited);
        Assert.True(released.Sum() >= 3);
    }
}
