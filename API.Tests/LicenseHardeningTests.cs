using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Controllers;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Extensions;
using Platform.Api.Security;
using Platform.Api.Services;
using Platform.Api.Services.Licensing;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class LicenseHardeningTests
{
    private const string LicenseKey = "HOSTEL-TEST-KEY1";
    private const string IntegrationKey = "ik_test_integration_key_001";

    private static ValidateLicenseRequest Req(string? device = null) =>
        new() { LicenseKey = LicenseKey, ServiceCode = "HOSTEL", DeviceId = device };

    private static JsonElement VerifyToken(string token, LicenseTokenSigner signer)
    {
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        var header = JsonDocument.Parse(B64(parts[0])).RootElement;
        Assert.Equal("ES256", header.GetProperty("alg").GetString());
        Assert.Contains("\"typ\":\"license+jwt\"", Encoding.UTF8.GetString(B64(parts[0])));
        var kid = header.GetProperty("kid").GetString();
        var jwk = Assert.Single(signer.PublicKeys, k => k.Kid == kid);
        using var ec = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = B64(jwk.X), Y = B64(jwk.Y) }
        });
        Assert.True(ec.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), B64(parts[2]), HashAlgorithmName.SHA256));
        return JsonDocument.Parse(B64(parts[1])).RootElement;
    }

    private static byte[] B64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    [Fact]
    public async Task ValidLicense_ReturnsVerifiableTokenWithClaimsAndGrace()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        var f = await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);
        var settings = LicenseValidationTestFactory.Settings();
        var signer = LicenseValidationTestFactory.Signer(settings);
        var r = await LicenseValidationTestFactory.Create(db, settings, signer: signer).ValidateAsync(IntegrationKey, Req("dev-1"));

        Assert.True(r.IsValid);
        Assert.Equal(LicenseValidationReasons.Ok, r.Reason);
        Assert.Equal(f.License.Id, r.LicenseId);
        Assert.NotNull(r.Token);
        var claims = VerifyToken(r.Token!, signer);
        Assert.Equal(f.License.Id, claims.GetProperty("license_id").GetString());
        Assert.Equal("HOSTEL", claims.GetProperty("product").GetString());
        Assert.Equal("Growth", claims.GetProperty("plan").GetString());
        Assert.Equal("dev-1", claims.GetProperty("device_id").GetString());
        var grace = r.OfflineGraceUntil!.Value;
        Assert.InRange((grace - r.ServerTime).TotalDays, 6.9, 7.01);
        Assert.True(r.RefreshAfter > r.ServerTime && r.RefreshAfter < grace);
        Assert.Equal(new DateTimeOffset(grace).ToUnixTimeSeconds(), claims.GetProperty("exp").GetInt64());
    }

    [Fact]
    public async Task Grace_IsCappedAtLicenseExpiry()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        var f = await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);
        f.License.ExpiresAt = DateTime.UtcNow.AddDays(2);
        await db.SaveChangesAsync();
        var r = await LicenseValidationTestFactory.Create(db).ValidateAsync(IntegrationKey, Req());
        Assert.True(r.IsValid);
        Assert.True(r.OfflineGraceUntil <= f.License.ExpiresAt!.Value.AddHours(24).AddSeconds(1));
        Assert.True((r.OfflineGraceUntil!.Value - r.ServerTime).TotalDays < 3.01);
    }

    [Theory]
    [InlineData(LicenseStatus.Revoked, "revoked")]
    [InlineData(LicenseStatus.Expired, "expired")]
    public async Task InvalidLicense_HasReasonAndNoToken(LicenseStatus status, string reason)
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        var f = await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);
        f.License.Status = status;
        await db.SaveChangesAsync();
        var r = await LicenseValidationTestFactory.Create(db).ValidateAsync(IntegrationKey, Req());
        Assert.False(r.IsValid);
        Assert.Equal(reason, r.Reason);
        Assert.Null(r.Token);
        Assert.NotEqual(default, r.ServerTime);
    }

    [Fact]
    public async Task BadIntegrationKey_HasReason()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);
        var r = await LicenseValidationTestFactory.Create(db).ValidateAsync("ik_wrong", Req());
        Assert.False(r.IsValid);
        Assert.Equal(LicenseValidationReasons.InvalidIntegrationKey, r.Reason);
    }

    [Fact]
    public async Task DeviceLimit_EnforcedReleaseFreesSlot_SameDeviceReuses()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        var f = await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);
        f.License.MaxDevices = 2;
        await db.SaveChangesAsync();
        var svc = LicenseValidationTestFactory.Create(db);

        Assert.True((await svc.ValidateAsync(IntegrationKey, Req("a"))).IsValid);
        Assert.True((await svc.ValidateAsync(IntegrationKey, Req("b"))).IsValid);
        Assert.True((await svc.ValidateAsync(IntegrationKey, Req("a"))).IsValid);
        var third = await svc.ValidateAsync(IntegrationKey, Req("c"));
        Assert.False(third.IsValid);
        Assert.Equal(LicenseValidationReasons.DeviceLimit, third.Reason);
        Assert.Equal(2, await db.LicenseDevices.CountAsync());
        Assert.True(await db.AuditLogs.CountAsync(a => a.Action == AuditAction.LicenseDeviceActivated) == 2);

        var device = await db.LicenseDevices.FirstAsync(d => d.DeviceIdHash == LicenseDeviceHasher.Hash("b"));
        device.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Assert.True((await svc.ValidateAsync(IntegrationKey, Req("c"))).IsValid);
        var reB = await svc.ValidateAsync(IntegrationKey, Req("b"));
        Assert.Equal(LicenseValidationReasons.DeviceLimit, reB.Reason);
    }

    [Fact]
    public async Task RequireDeviceId_RejectsMissingDevice()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);
        var s = LicenseValidationTestFactory.Settings(x => x.RequireDeviceId = true);
        var r = await LicenseValidationTestFactory.Create(db, s).ValidateAsync(IntegrationKey, Req());
        Assert.Equal(LicenseValidationReasons.DeviceRequired, r.Reason);
        Assert.False(r.IsValid);
    }

    [Fact]
    public async Task Infrastructure_FailureBecomesUnavailable()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1")
            .Options;
        await using var db = new AppDbContext(options);
        var svc = LicenseValidationTestFactory.Create(db, LicenseValidationTestFactory.Settings(x => x.ValidationTimeoutSeconds = 3));
        await Assert.ThrowsAsync<LicenseValidationUnavailableException>(() => svc.ValidateAsync(IntegrationKey, Req()));
    }

    [Fact]
    public async Task FrozenDatabase_StillAnswersWithinDeadline()
    {
        // A TCP server that accepts and then never answers behaves like a paused/frozen Postgres.
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var held = new List<System.Net.Sockets.TcpClient>();
        _ = Task.Run(async () =>
        {
            try { while (true) held.Add(await listener.AcceptTcpClientAsync()); } catch { /* stopped */ }
        });
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql($"Host=127.0.0.1;Port={port};Database=x;Username=x;Password=x;Timeout=4;Command Timeout=4;Pooling=false")
                .Options;
            await using var db = new AppDbContext(options);
            var svc = LicenseValidationTestFactory.Create(db, LicenseValidationTestFactory.Settings(x => x.ValidationTimeoutSeconds = 1));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAsync<LicenseValidationUnavailableException>(() => svc.ValidateAsync(IntegrationKey, Req()));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"took {sw.Elapsed}");
            await svc.DisposeAsync(); // as the request scope would, before the DbContext is disposed
        }
        finally
        {
            listener.Stop();
            foreach (var c in held) c.Dispose();
        }
    }

    [Fact]
    public void InfrastructureClassifier_RecognisesNestedFailures()
    {
        Assert.True(LicenseValidationUnavailableException.IsInfrastructureFailure(new InvalidOperationException("x", new TimeoutException())));
        Assert.True(LicenseValidationUnavailableException.IsInfrastructureFailure(new System.Net.Sockets.SocketException()));
        Assert.False(LicenseValidationUnavailableException.IsInfrastructureFailure(new ArgumentException()));
    }

    [Fact]
    public void Signer_ProductionWithoutKey_OmitsToken_RequiredThrows_InvalidPemThrows()
    {
        var none = LicenseValidationTestFactory.Signer(new LicensingSettings(), "Production");
        Assert.False(none.CanSign);
        Assert.Empty(none.PublicKeys);
        Assert.Throws<InvalidOperationException>(() =>
            LicenseValidationTestFactory.Signer(new LicensingSettings { RequireTokenSigning = true }, "Production"));
        Assert.ThrowsAny<Exception>(() =>
            LicenseValidationTestFactory.Signer(new LicensingSettings { TokenSigningKey = "not a pem" }, "Production"));
        var dev = LicenseValidationTestFactory.Signer(new LicensingSettings(), "Development");
        Assert.True(dev.CanSign);
    }

    [Fact]
    public void Signer_PublishesAdditionalKeysForRotation_AndAcceptsEscapedNewlines()
    {
        var (_, oldPub, oldKid) = LicenseTokenSigner.GenerateKeyPair();
        var (priv, _, kid) = LicenseTokenSigner.GenerateKeyPair();
        var signer = LicenseValidationTestFactory.Signer(new LicensingSettings
        {
            TokenSigningKey = priv.Replace("\n", "\\n"),
            AdditionalPublicKeys = [new() { Kid = oldKid, PublicKeyPem = oldPub }]
        });
        Assert.Equal(kid, signer.ActiveKid);
        Assert.Equal(2, signer.PublicKeys.Count);
        Assert.Contains(signer.PublicKeys, k => k.Kid == oldKid);
    }

    [Fact]
    public void RateLimiter_PerLicenseKey_Then_Retries()
    {
        using var limiter = new LicenseValidationRateLimiter(Options.Create(new LicensingSettings { LicenseKeyPermitLimit = 2, PerIpPermitLimit = 100 }));
        Assert.Null(limiter.TryAcquire("1.1.1.1", "K"));
        Assert.Null(limiter.TryAcquire("2.2.2.2", "K"));
        var wait = limiter.TryAcquire("3.3.3.3", "K");
        Assert.NotNull(wait);
        Assert.True(wait > TimeSpan.Zero);
        Assert.Null(limiter.TryAcquire("3.3.3.3", "OTHER"));
    }

    [Fact]
    public void IntegrationKeyCache_InvalidateAll_DropsEntriesAndStaleWrites()
    {
        var cache = new IntegrationKeyCache(Options.Create(new LicensingSettings()));
        var gen = cache.Generation;
        cache.Set("k", new IntegrationKeyCache.Entry("id", "p", "HOSTEL", null), gen);
        Assert.True(cache.TryGet("k", out _));
        cache.InvalidateAll();
        Assert.False(cache.TryGet("k", out _));
        cache.Set("k", new IntegrationKeyCache.Entry("id", "p", "HOSTEL", null), gen); // stale generation
        Assert.False(cache.TryGet("k", out _));
    }

    [Fact]
    public async Task RevokedIntegrationKey_StopsWorkingImmediately()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);
        var cache = new IntegrationKeyCache(Options.Create(LicenseValidationTestFactory.Settings()));
        var svc = LicenseValidationTestFactory.Create(db, cache: cache);
        Assert.True((await svc.ValidateAsync(IntegrationKey, Req())).IsValid);
        var key = await db.IntegrationKeys.SingleAsync();
        key.IsActive = false;
        await db.SaveChangesAsync();
        cache.InvalidateAll();
        Assert.Equal(LicenseValidationReasons.InvalidIntegrationKey, (await svc.ValidateAsync(IntegrationKey, Req())).Reason);
    }

    private sealed class ThrowingValidation : ILicenseValidationService
    {
        public Task<ValidateLicenseResponse> ValidateAsync(string h, ValidateLicenseRequest r, CancellationToken ct = default) =>
            throw new LicenseValidationUnavailableException("down");
    }

    private static LicenseValidationController Controller(ILicenseValidationService svc, LicensingSettings s)
    {
        var c = new LicenseValidationController(svc, new LicenseValidationRateLimiter(Options.Create(s)),
            LicenseValidationTestFactory.Signer(s), Options.Create(s));
        var http = new DefaultHttpContext();
        http.Request.Headers[LicenseValidationController.IntegrationKeyHeader] = IntegrationKey;
        http.Connection.RemoteIpAddress = IPAddress.Parse("9.9.9.9");
        c.ControllerContext = new ControllerContext { HttpContext = http };
        return c;
    }

    [Fact]
    public async Task Controller_Returns503WithRetryAfter()
    {
        var c = Controller(new ThrowingValidation(), LicenseValidationTestFactory.Settings(x => x.UnavailableRetryAfterSeconds = 17));
        var result = await c.Validate(Req(), CancellationToken.None);
        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, obj.StatusCode);
        Assert.Equal("17", c.Response.Headers.RetryAfter.ToString());
        Assert.Equal(LicenseValidationReasons.ServiceUnavailable, Assert.IsType<ValidateLicenseErrorResponse>(obj.Value).Reason);
    }

    [Fact]
    public async Task Controller_Returns429WithRetryAfter()
    {
        var c = Controller(new ThrowingValidation(), LicenseValidationTestFactory.Settings(x => x.LicenseKeyPermitLimit = 1));
        await c.Validate(Req(), CancellationToken.None);
        var result = await c.Validate(Req(), CancellationToken.None);
        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(429, obj.StatusCode);
        Assert.False(string.IsNullOrEmpty(c.Response.Headers.RetryAfter.ToString()));
        Assert.Equal(LicenseValidationReasons.RateLimited, Assert.IsType<ValidateLicenseErrorResponse>(obj.Value).Reason);
    }

    [Fact]
    public void Jwks_ListsSigningKey()
    {
        var s = LicenseValidationTestFactory.Settings();
        var c = Controller(new ThrowingValidation(), s);
        var ok = Assert.IsType<OkObjectResult>(c.Keys());
        var json = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("test-kid", json);
        Assert.Contains("ES256", json);
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    public void IsPrivate_Works(string ip, bool expected) =>
        Assert.Equal(expected, ForwardedHeadersSetup.IsPrivate(IPAddress.Parse(ip)));

    [Fact]
    public async Task AdminRelease_ListsDevices_FreesSlot_IsIdempotentAndAudited()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        var f = await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);
        f.License.MaxDevices = 1;
        await db.SaveChangesAsync();
        var svc = LicenseValidationTestFactory.Create(db);
        Assert.True((await svc.ValidateAsync(IntegrationKey, Req("a"))).IsValid);
        Assert.Equal(LicenseValidationReasons.DeviceLimit, (await svc.ValidateAsync(IntegrationKey, Req("b"))).Reason);

        var h = LicenseActivationTests.Create(db);
        var listed = Assert.Single(await h.Licenses.ListDevicesAsync(f.License.Id));
        Assert.True(listed.IsActive);
        Assert.Equal(8, listed.Fingerprint.Length);

        var released = await h.Licenses.ReleaseDeviceAsync(f.License.Id, listed.Id, "admin@test");
        Assert.False(released.IsActive);
        await h.Licenses.ReleaseDeviceAsync(f.License.Id, listed.Id, "admin@test");
        Assert.Single(h.Audit.Actions, a => a == AuditAction.LicenseDeviceReleased);
        Assert.True((await svc.ValidateAsync(IntegrationKey, Req("b"))).IsValid);
        await Assert.ThrowsAsync<NotFoundException>(() => h.Licenses.ReleaseDeviceAsync(f.License.Id, "nope", "admin@test"));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(3, 3)]
    [InlineData(5000, 1000)]
    public void ResolveMaxDevices_MapsZeroToUnlimitedAndCaps(int input, int? expected) =>
        Assert.Equal(expected, LicenseService.ResolveMaxDevices(input));

    [Fact]
    public async Task Postgres_ConcurrentNewDevicesNeverExceedMaxDevices()
    {
        await using var pg = await PostgresBehaviorTests.TempDatabase.CreateAsync();
        if (pg is null)
            return;
        string licenseId;
        await using (var seed = pg.NewContext())
        {
            var customer = new Customer { Name = "Acme", ContactEmail = "a@acme.test" };
            var product = new ServiceProduct { Name = "Hostel", Code = "HOSTEL" };
            var license = new License
            {
                CustomerId = customer.Id, ServiceProductId = product.Id, Status = LicenseStatus.Active,
                PlanName = "Growth", ExpiresAt = DateTime.UtcNow.AddDays(30), MaxDevices = 3,
                LicenseKeyHash = KeyHasher.Hash(LicenseKey),
                LicenseKeyLookupHash = KeyLookupHasher.ComputeSha256Hex(LicenseKey)
            };
            seed.AddRange(customer, product, license, new IntegrationKey
            {
                ServiceProductId = product.Id, KeyHash = KeyHasher.Hash(IntegrationKey),
                KeyLookupHash = KeyLookupHasher.ComputeSha256Hex(IntegrationKey), IsActive = true
            });
            await seed.SaveChangesAsync();
            licenseId = license.Id;
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            await using var db = pg.NewContext();
            return await LicenseValidationTestFactory.Create(db).ValidateAsync(IntegrationKey, Req($"dev-{i % 6}"));
        }));

        await using var verify = pg.NewContext();
        var stored = await verify.LicenseDevices.IgnoreQueryFilters().Where(d => d.LicenseId == licenseId).ToListAsync();
        Assert.Equal(3, stored.Count);
        var winners = stored.Select(d => d.DeviceIdHash).ToHashSet();
        for (var i = 0; i < 12; i++)
        {
            var mine = winners.Contains(LicenseDeviceHasher.Hash($"dev-{i % 6}"));
            Assert.Equal(mine, results[i].IsValid);
            if (!mine)
                Assert.Equal(LicenseValidationReasons.DeviceLimit, results[i].Reason);
        }
        Assert.Equal(3, await verify.AuditLogs.CountAsync(a => a.Action == AuditAction.LicenseDeviceActivated));
        Console.WriteLine("POSTGRES_DEVICE_RACE_RAN");
    }

    [Fact]
    public async Task MismatchedServiceCode_ReportsWrongService_NotInvalidIntegrationKey()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);
        var svc = LicenseValidationTestFactory.Create(db);
        var r = await svc.ValidateAsync(IntegrationKey, new ValidateLicenseRequest { LicenseKey = LicenseKey, ServiceCode = "OTHER" });
        Assert.False(r.IsValid);
        Assert.Equal(LicenseValidationReasons.WrongService, r.Reason);
        Assert.Null(r.LicenseId);
        // Same cached integration key still validates with the right (or no / differently cased) code.
        Assert.True((await svc.ValidateAsync(IntegrationKey, new ValidateLicenseRequest { LicenseKey = LicenseKey, ServiceCode = "hostel" })).IsValid);
        Assert.True((await svc.ValidateAsync(IntegrationKey, new ValidateLicenseRequest { LicenseKey = LicenseKey })).IsValid);
    }

    [Fact]
    public async Task PastedKeysWithWhitespace_StillValidate()
    {
        await using var db = LicenseValidationServiceTests.CreateDbContext();
        await LicenseValidationServiceTests.SeedActiveLicenseAsync(db);
        var r = await LicenseValidationTestFactory.Create(db).ValidateAsync(" " + IntegrationKey + "\n",
            new ValidateLicenseRequest { LicenseKey = "  " + LicenseKey + " \t", ServiceCode = "HOSTEL" });
        Assert.True(r.IsValid);
    }

    [Fact]
    public void RateLimiter_PerLicenseBudget_IsNotExhaustedByForeignIntegrationKeys()
    {
        using var limiter = new LicenseValidationRateLimiter(Options.Create(new LicensingSettings { LicenseKeyPermitLimit = 2, PerIpPermitLimit = 100 }));
        Assert.Null(limiter.TryAcquire("9.9.9.9", "K", "ik_junk"));
        Assert.Null(limiter.TryAcquire("9.9.9.9", "K", "ik_junk"));
        Assert.NotNull(limiter.TryAcquire("9.9.9.9", "K", "ik_junk"));
        Assert.Null(limiter.TryAcquire("1.1.1.1", "K", "ik_real"));
        Assert.Null(limiter.TryAcquire("1.1.1.1", " K ", "ik_real"));
        Assert.NotNull(limiter.TryAcquire("1.1.1.1", "K", "ik_real"));
    }
}
