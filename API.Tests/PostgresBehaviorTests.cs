using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Security;
using Platform.Api.Services;
using Platform.Api.Services.Billing;
using Platform.Api.Services.Email;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

/// <summary>
/// Behaviour that only exists on a real PostgreSQL server (row locks, SKIP LOCKED, ExecuteUpdate/Delete,
/// real transactions). Each test gets its own throw-away database built from the EF *migrations*
/// (so the migration chain is exercised too). Silently returns when PLATFORM_TEST_DB is not set/reachable,
/// like the other Postgres tests.
/// </summary>
public class PostgresBehaviorTests
{
    [Fact]
    public async Task ConcurrentLegacyKeyValidations_UpgradeHashesWithoutBumpingLicenseVersion()
    {
        await using var pg = await TempDatabase.CreateAsync();
        if (pg is null)
            return;

        const string licenseKey = "HOSTEL-PG-KEY1";
        const string integrationKey = "ik_pg_integration_key_001";
        string licenseId;
        long versionBefore;
        await using (var seed = pg.NewContext())
        {
            var customer = new Customer { Name = "Acme", ContactEmail = "a@acme.test" };
            var product = new ServiceProduct { Name = "Hostel", Code = "HOSTEL" };
            var license = new License
            {
                CustomerId = customer.Id,
                ServiceProductId = product.Id,
                Status = LicenseStatus.Active,
                PlanName = "Growth",
                ExpiresAt = DateTime.UtcNow.AddDays(30),
                LicenseKeyHash = BCrypt.Net.BCrypt.HashPassword(licenseKey, 4),
                LicenseKeyLookupHash = KeyLookupHasher.ComputeSha256Hex(licenseKey)
            };
            seed.AddRange(customer, product, license, new IntegrationKey
            {
                ServiceProductId = product.Id,
                KeyHash = BCrypt.Net.BCrypt.HashPassword(integrationKey, 4),
                KeyLookupHash = KeyLookupHasher.ComputeSha256Hex(integrationKey),
                IsActive = true
            });
            await seed.SaveChangesAsync();
            licenseId = license.Id;
            versionBefore = license.Version;
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = pg.NewContext();
            var service = new LicenseValidationService(
                db,
                new AllowAllDenyList(),
                new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
                Options.Create(new RedisSettings { ValidationCacheSeconds = 60 }),
                NullLogger<LicenseValidationService>.Instance);
            return await service.ValidateAsync(integrationKey, new ValidateLicenseRequest { LicenseKey = licenseKey, ServiceCode = "HOSTEL" });
        }));

        Assert.All(results, r => Assert.True(r.IsValid));

        await using var verify = pg.NewContext();
        var stored = await verify.Licenses.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Id == licenseId);
        Assert.StartsWith("v2$", stored.LicenseKeyHash);
        Assert.True(KeyHasher.Verify(licenseKey, stored.LicenseKeyHash, out var needsUpgrade));
        Assert.False(needsUpgrade);
        Assert.Equal(versionBefore, stored.Version); // the upgrade must not look like an admin edit
        var key = await verify.IntegrationKeys.AsNoTracking().SingleAsync();
        Assert.StartsWith("v2$", key.KeyHash);
        Assert.True(KeyHasher.Verify(integrationKey, key.KeyHash, out _));

        // A second validation now uses the cheap v2 path and still works.
        await using var again = pg.NewContext();
        var second = await new LicenseValidationService(
            again, new AllowAllDenyList(),
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
            Options.Create(new RedisSettings()), NullLogger<LicenseValidationService>.Instance)
            .ValidateAsync(integrationKey, new ValidateLicenseRequest { LicenseKey = licenseKey });
        Assert.True(second.IsValid);
    }

    [Fact]
    public async Task ConcurrentInvoiceCreation_YieldsUniqueNumbers()
    {
        await using var pg = await TempDatabase.CreateAsync();
        if (pg is null)
            return;

        string customerId;
        await using (var seed = pg.NewContext())
        {
            var customer = new Customer { Name = "Acme", ContactEmail = "a@acme.test" };
            seed.Customers.Add(customer);
            await seed.SaveChangesAsync();
            customerId = customer.Id;
        }

        var numbers = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var db = pg.NewContext();
            var billing = new BillingService(db, new AuditLogService(db), new EmailOutboxService(db),
                new EmailTemplateService(), new AllowAllDenyList());
            var invoice = await billing.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                CustomerId = customerId,
                Currency = "usd",
                Subtotal = 10m,
                Status = InvoiceStatus.Sent
            }, "admin@test.com");
            return (invoice.InvoiceNumber, invoice.Currency);
        }));

        Assert.Equal(12, numbers.Select(n => n.InvoiceNumber).Distinct().Count());
        Assert.All(numbers, n => Assert.Equal("USD", n.Currency));
        var year = DateTime.UtcNow.Year;
        Assert.Equal(
            Enumerable.Range(1, 12).Select(i => $"INV-{year}-{i:D5}"),
            numbers.Select(n => n.InvoiceNumber).OrderBy(n => n));
    }

    [Fact]
    public async Task OutboxClaim_ConcurrentWorkersNeverClaimTheSameMessage()
    {
        await using var pg = await TempDatabase.CreateAsync();
        if (pg is null)
            return;

        await using (var seed = pg.NewContext())
        {
            for (var i = 0; i < 60; i++)
            {
                seed.EmailOutboxMessages.Add(new EmailOutboxMessage
                {
                    Kind = EmailDeliveryKind.Welcome,
                    ToEmail = $"u{i}@x.test",
                    Subject = "s",
                    HtmlBody = "b",
                    Status = EmailDeliveryStatus.Pending,
                    NextAttemptAt = DateTime.UtcNow.AddMinutes(-1)
                });
            }

            await seed.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(pg.ConnectionString));
        await using var provider = services.BuildServiceProvider();
        var worker = new EmailOutboxWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EmailSettings { Outbox = new EmailOutboxSettings { BatchSize = 15 } }),
            NullLogger<EmailOutboxWorker>.Instance);

        var claimed = (await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => worker.ClaimBatchAsync(CancellationToken.None)))))
            .SelectMany(x => x).ToList();

        Assert.Equal(claimed.Count, claimed.Distinct().Count());
        Assert.Equal(60, claimed.Count); // 8 workers x up to 15 each drain all 60, none twice
        await using var verify = pg.NewContext();
        Assert.Equal(60, await verify.EmailOutboxMessages.CountAsync(m => m.Status == EmailDeliveryStatus.Sending));
    }

    [Fact]
    public async Task OutboxPurge_DeletesLargeBacklogInOnePass()
    {
        await using var pg = await TempDatabase.CreateAsync();
        if (pg is null)
            return;

        var old = DateTime.UtcNow.AddDays(-200);
        await using (var seed = pg.NewContext())
        {
            for (var i = 0; i < 1200; i++)
            {
                seed.EmailOutboxMessages.Add(new EmailOutboxMessage
                {
                    Kind = EmailDeliveryKind.Welcome, ToEmail = "u@x.test", Subject = "s", HtmlBody = "b",
                    Status = i % 2 == 0 ? EmailDeliveryStatus.Sent : EmailDeliveryStatus.Cancelled,
                    UpdatedAt = old
                });
            }

            seed.EmailOutboxMessages.Add(new EmailOutboxMessage
            {
                Kind = EmailDeliveryKind.Welcome, ToEmail = "keep@x.test", Subject = "s", HtmlBody = "b",
                Status = EmailDeliveryStatus.Sent, UpdatedAt = DateTime.UtcNow
            });
            seed.EmailOutboxMessages.Add(new EmailOutboxMessage
            {
                Kind = EmailDeliveryKind.Welcome, ToEmail = "failed@x.test", Subject = "s", HtmlBody = "b",
                Status = EmailDeliveryStatus.Failed, UpdatedAt = old
            });
            await seed.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(pg.ConnectionString));
        await using var provider = services.BuildServiceProvider();
        var worker = new EmailOutboxWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new EmailSettings()),
            NullLogger<EmailOutboxWorker>.Instance);

        await worker.PurgeAsync(CancellationToken.None);

        await using var verify = pg.NewContext();
        Assert.Equal(2, await verify.EmailOutboxMessages.CountAsync()); // recent Sent + old Failed survive
    }

    [Fact]
    public async Task ExpireDueLicenses_BatchesOnPostgres()
    {
        await using var pg = await TempDatabase.CreateAsync();
        if (pg is null)
            return;

        await using var db = pg.NewContext();
        // One open license per customer+service is a unique index, so each due license gets its own customer.
        var product = new ServiceProduct { Name = "Hostel", Code = "HOSTEL" };
        db.ServiceProducts.Add(product);
        for (var i = 0; i < 451; i++)
        {
            var customer = new Customer { Name = $"Acme {i}", ContactEmail = $"a{i}@acme.test" };
            db.Customers.Add(customer);
            db.Licenses.Add(new License
            {
                CustomerId = customer.Id, ServiceProductId = product.Id, PlanName = "Pro",
                Status = LicenseStatus.Active,
                ExpiresAt = i < 450 ? DateTime.UtcNow.AddHours(-2) : DateTime.UtcNow.AddDays(3)
            });
        }

        await db.SaveChangesAsync();

        var outbox = new EmailOutboxService(db);
        var templates = new EmailTemplateService();
        var protector = new EmailPayloadProtector(
            Options.Create(new EmailSettings { Outbox = new EmailOutboxSettings { EncryptionKey = Convert.ToBase64String(new byte[32]) } }),
            new ConfigurationBuilder().Build(), new TestEnv());
        var service = new LicenseService(db, null!, new AuditLogService(db),
            new LicenseKeyDeliveryService(db, outbox, protector, templates), new AllowAllDenyList(), outbox, templates);

        Assert.Equal(450, await service.ExpireDueLicensesAsync());
        Assert.Equal(0, await service.ExpireDueLicensesAsync());

        await using var verify = pg.NewContext();
        Assert.Equal(450, await verify.Licenses.IgnoreQueryFilters().CountAsync(l => l.Status == LicenseStatus.Expired));
        Assert.Equal(1, await verify.Licenses.IgnoreQueryFilters().CountAsync(l => l.Status == LicenseStatus.Active));
        Assert.Equal(450, await verify.AuditLogs.CountAsync(a => a.Action == AuditAction.LicenseExpired));
    }

    private sealed class TestEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "API.Tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class AllowAllDenyList : ILicenseDenyListService
    {
        public Task DenyLicenseAsync(string licenseId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DenyCustomerLicensesAsync(string customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsDeniedAsync(string licenseId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ClearLicenseDenyAsync(string licenseId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearCustomerDenyAsync(string customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>A uniquely named database created from the EF migrations and dropped on dispose.</summary>
    internal sealed class TempDatabase : IAsyncDisposable
    {
        private readonly string _adminConnection;
        private readonly string _name;

        private TempDatabase(string adminConnection, string name, string connectionString)
        {
            _adminConnection = adminConnection;
            _name = name;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public AppDbContext NewContext() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

        public static async Task<TempDatabase?> CreateAsync()
        {
            var baseConnection = Environment.GetEnvironmentVariable("PLATFORM_TEST_DB");
            if (string.IsNullOrWhiteSpace(baseConnection))
                return null;

            var name = "platform_test_" + Guid.NewGuid().ToString("N")[..12];
            try
            {
                var admin = new NpgsqlConnectionStringBuilder(baseConnection) { Pooling = false };
                await using (var conn = new NpgsqlConnection(admin.ConnectionString))
                {
                    await conn.OpenAsync();
                    await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
                    await cmd.ExecuteNonQueryAsync();
                }

                var target = new NpgsqlConnectionStringBuilder(baseConnection) { Database = name, MaxPoolSize = 40 };
                var temp = new TempDatabase(admin.ConnectionString, name, target.ConnectionString);
                await using var db = temp.NewContext();
                await db.Database.MigrateAsync();
                return temp;
            }
            catch
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            try
            {
                await using var conn = new NpgsqlConnection(_adminConnection);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", conn);
                await cmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // best effort cleanup
            }
        }
    }
}
