using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Api.Services.Email;
using Platform.Shared.Dtos.Audit;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class LicenseExpiryBatchTests
{
    [Fact]
    public async Task ExpireDueLicensesAsync_ExpiresAllDueInBatchesWithAuditAndDenyList()
    {
        // P7
        var dbName = NUlid.Ulid.NewUlid().ToString();
        await using var db = CreateDb(dbName);
        var (customer, product) = await SeedBaseAsync(db);
        var due = new List<License>();
        for (var i = 0; i < 450; i++)
            due.Add(NewLicense(customer, product, DateTime.UtcNow.AddHours(-1 - i % 5)));
        var future = NewLicense(customer, product, DateTime.UtcNow.AddDays(5));
        var todayMidnight = NewLicense(customer, product, DateTime.UtcNow.Date); // still valid today
        var pending = NewLicense(customer, product, DateTime.UtcNow.AddDays(-3), LicenseStatus.Pending);
        db.Licenses.AddRange(due.Append(future).Append(todayMidnight).Append(pending));
        await db.SaveChangesAsync();

        var denyList = new RecordingDenyList();
        var service = CreateService(db, new AuditLogService(db), denyList);

        var count = await service.ExpireDueLicensesAsync();

        Assert.Equal(450, count);
        Assert.Equal(450, denyList.Denied.Count);
        Assert.Equal(450, await db.Licenses.IgnoreQueryFilters().CountAsync(l => l.Status == LicenseStatus.Expired));
        Assert.Equal(LicenseStatus.Active, (await db.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == future.Id)).Status);
        Assert.Equal(LicenseStatus.Active, (await db.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == todayMidnight.Id)).Status);
        Assert.Equal(LicenseStatus.Pending, (await db.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == pending.Id)).Status);
        Assert.Equal(450, await db.AuditLogs.CountAsync(a => a.Action == AuditAction.LicenseExpired
            && a.PerformedBy == "system:license-expiry" && a.LicenseId != null));

        // Second run has nothing left to do.
        Assert.Equal(0, await service.ExpireDueLicensesAsync());
    }

    [Fact]
    public async Task ExpireDueLicensesAsync_SkipsLicenseChangedByAnotherWriterMidBatch()
    {
        var dbName = NUlid.Ulid.NewUlid().ToString();
        await using var db = CreateDb(dbName);
        var (customer, product) = await SeedBaseAsync(db);
        var a = NewLicense(customer, product, DateTime.UtcNow.AddDays(-1));
        var b = NewLicense(customer, product, DateTime.UtcNow.AddDays(-1));
        db.Licenses.AddRange(a, b);
        await db.SaveChangesAsync();

        var denyList = new RecordingDenyList();
        var racing = new RacingAuditService(new AuditLogService(db), dbName, a.Id);
        var service = CreateService(db, racing, denyList);

        await service.ExpireDueLicensesAsync();

        // 'a' was revoked by someone else before our save: it must stay revoked and must not be denied.
        // (The EF InMemory provider is not transactional, so a partially applied batch
        // can make 'b' look already expired on the retry and leave stray audit rows; on PostgreSQL the failed batch rolls back and 'b'
        // is expired by the one-at-a-time fallback. Both end states satisfy these assertions.)
        await using var verify = CreateDb(dbName);
        Assert.Equal(LicenseStatus.Revoked, (await verify.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == a.Id)).Status);
        Assert.Equal(LicenseStatus.Expired, (await verify.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == b.Id)).Status);
        Assert.DoesNotContain(a.Id, denyList.Denied);
    }

    private static License NewLicense(Customer c, ServiceProduct p, DateTime expires, LicenseStatus status = LicenseStatus.Active) => new()
    {
        CustomerId = c.Id, Customer = c,
        ServiceProductId = p.Id, ServiceProduct = p,
        PlanName = "Pro", Status = status, ExpiresAt = expires
    };

    private static async Task<(Customer, ServiceProduct)> SeedBaseAsync(AppDbContext db)
    {
        var customer = new Customer { Name = "Acme", ContactEmail = "a@acme.test" };
        var product = new ServiceProduct { Name = "Hostel", Code = "HOSTEL" };
        db.AddRange(customer, product);
        await db.SaveChangesAsync();
        return (customer, product);
    }

    private static AppDbContext CreateDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static LicenseService CreateService(AppDbContext db, IAuditLogService audit, ILicenseDenyListService denyList)
    {
        var outbox = new EmailOutboxService(db);
        var templates = new EmailTemplateService();
        var protector = new EmailPayloadProtector(
            Options.Create(new EmailSettings
            {
                Outbox = new EmailOutboxSettings { EncryptionKey = Convert.ToBase64String(new byte[32]) }
            }),
            new ConfigurationBuilder().Build(),
            new Env());
        var keyDelivery = new LicenseKeyDeliveryService(db, outbox, protector, templates);
        return new LicenseService(db, null!, audit, keyDelivery, denyList, outbox, templates);
    }

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "API.Tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    /// <summary>Revokes one license through a second context just before the batch is saved.</summary>
    private sealed class RacingAuditService(IAuditLogService inner, string dbName, string licenseId) : IAuditLogService
    {
        private bool _raced;

        public Task<IReadOnlyList<AuditLogDto>> ListAsync(string? customerId = null, string? licenseId = null,
            AuditAction? action = null, int limit = 100, CancellationToken cancellationToken = default) =>
            inner.ListAsync(customerId, licenseId, action, limit, cancellationToken);

        public Task WriteAsync(AuditAction action, string performedBy, string? customerId = null, string? licenseId = null,
            string? invoiceId = null, string? detailsJson = null, string? ipAddress = null,
            CancellationToken cancellationToken = default) =>
            inner.WriteAsync(action, performedBy, customerId, licenseId, invoiceId, detailsJson, ipAddress, cancellationToken);

        public async Task WriteBatchAsync(IReadOnlyList<AuditEntryRequest> entries, CancellationToken cancellationToken = default)
        {
            if (!_raced)
            {
                _raced = true;
                await using var other = CreateDb(dbName);
                var license = await other.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == licenseId, cancellationToken);
                license.Status = LicenseStatus.Revoked;
                await other.SaveChangesAsync(cancellationToken);
            }

            await inner.WriteBatchAsync(entries, cancellationToken);
        }
    }

    private sealed class RecordingDenyList : ILicenseDenyListService
    {
        public HashSet<string> Denied { get; } = [];
        private readonly object _gate = new();

        public Task DenyLicenseAsync(string licenseId, CancellationToken cancellationToken = default)
        {
            lock (_gate) Denied.Add(licenseId);
            return Task.CompletedTask;
        }

        public Task DenyCustomerLicensesAsync(string customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsDeniedAsync(string licenseId, CancellationToken cancellationToken = default) => Task.FromResult(Denied.Contains(licenseId));
        public Task ClearLicenseDenyAsync(string licenseId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearCustomerDenyAsync(string customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
