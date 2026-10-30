using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Api.Services.Renewals;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Enums;
using Xunit;
using static API.Tests.RenewalTests;

namespace API.Tests;

/// <summary>Renewal concurrency on real PostgreSQL (advisory/row locks, partial unique index). Skips without Postgres.</summary>
public class RenewalPostgresTests
{
    // Own database so migrations run cleanly next to suites that use EnsureCreated on the shared one.
    private static readonly string ConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("PLATFORM_TEST_DB")
        ?? "Host=localhost;Port=5432;Database=platform_payment_test;Username=platform;Password=platform_dev")
    {
        Database = "platform_renewal_test"
    }.ConnectionString;

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options);

    private static async Task<bool> MigrateAsync()
    {
        try
        {
            await using var db = NewContext();
            await db.Database.MigrateAsync();
            return true;
        }
        catch when (Environment.GetEnvironmentVariable("PLATFORM_TEST_DB") is null)
        {
            return false; // no Postgres configured: skip; when PLATFORM_TEST_DB is set, failures surface
        }
    }

    [Fact]
    public async Task ConcurrentWorkers_CreateExactlyOneRenewalInvoice()
    {
        if (!await MigrateAsync())
            return;
        string licenseId;
        var expiry = DateTime.UtcNow.Date.AddDays(5);
        await using (var seed = NewContext())
            licenseId = (await SeedLicenseAsync(seed, expiry)).Id;

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var db = NewContext();
            try
            {
                return await CreateBilling(db).CreateRenewalInvoiceAsync(licenseId, expiry, BillingService.RenewalActor);
            }
            catch (ConflictException)
            {
                return null;
            }
        }));

        Assert.Single(results, r => r is not null);
        await using var check = NewContext();
        Assert.Equal(1, await check.Invoices.IgnoreQueryFilters().CountAsync(i => i.LicenseId == licenseId));
    }

    [Fact]
    public async Task UniqueIndex_RejectsSecondLiveRenewal_ButAllowsAfterVoid()
    {
        if (!await MigrateAsync())
            return;
        await using var db = NewContext();
        var expiry = DateTime.UtcNow.Date.AddDays(5);
        var license = await SeedLicenseAsync(db, expiry);
        var billing = CreateBilling(db);
        var first = await billing.CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);

        db.Invoices.Add(new Invoice
        {
            CustomerId = license.CustomerId, LicenseId = license.Id, InvoiceNumber = "DUP-" + NUlid.Ulid.NewUlid(),
            Currency = "GHS", Subtotal = 1, TotalAmount = 1, Status = InvoiceStatus.Sent, RenewalForExpiresAt = expiry
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        await billing.VoidInvoiceAsync(first!.Id, "admin");
        Assert.NotNull(await billing.CreateRenewalInvoiceAsync(license.Id, null, "admin", explicitRequest: true));
    }

    [Fact]
    public async Task ConcurrentPaystackAndManualReceipt_ExtendOnce()
    {
        if (!await MigrateAsync())
            return;
        string licenseId;
        InvoiceDto invoice;
        var expiry = DateTime.UtcNow.Date.AddDays(5);
        await using (var seed = NewContext())
        {
            licenseId = (await SeedLicenseAsync(seed, expiry)).Id;
            invoice = (await CreateBilling(seed).CreateRenewalInvoiceAsync(licenseId, expiry, BillingService.RenewalActor))!;
        }

        var outcomes = await Task.WhenAll(
            Pay("paystack:PLT-1", PaymentMethod.MobileMoney),
            Pay("manual-1", PaymentMethod.BankTransfer));

        await using var check = NewContext();
        var license = await check.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == licenseId);
        Assert.Equal(PeriodMath.AddMonths(expiry, 12), license.ExpiresAt);
        Assert.Contains(true, outcomes);

        async Task<bool> Pay(string key, PaymentMethod method)
        {
            await using var db = NewContext();
            try
            {
                await CreateBilling(db).RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
                {
                    IdempotencyKey = key, AmountPaid = invoice.TotalAmount, PaymentMethod = method, PaymentReference = key
                }, "admin");
                return true;
            }
            catch (InvalidOperationException)
            {
                return false; // second full payment refused (overpayment) — fine, renewal must still apply once
            }
        }
    }
}
