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
}
