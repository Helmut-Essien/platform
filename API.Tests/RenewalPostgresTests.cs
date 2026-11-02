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
public class RenewalPostgresTests : IAsyncLifetime
{
    private PostgresBehaviorTests.TempDatabase? _pg;

    public async Task InitializeAsync() => _pg = await PostgresBehaviorTests.TempDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_pg is not null)
            await _pg.DisposeAsync();
    }

    private AppDbContext NewContext() => _pg!.NewContext();

    private Task<bool> MigrateAsync() => Task.FromResult(_pg is not null);

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

    [Fact]
    public async Task StaleRenewalVoid_RacingPayment_NeverVoidsAPaidInvoice()
    {
        if (!await MigrateAsync())
            return;
        for (var round = 0; round < 5; round++)
        {
            string licenseId, staleId;
            var expiry = DateTime.UtcNow.Date.AddDays(10);
            var manual = DateTime.UtcNow.Date.AddDays(12);
            await using (var seed = NewContext())
            {
                var license = await SeedLicenseAsync(seed, expiry);
                licenseId = license.Id;
                staleId = (await CreateBilling(seed).CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor))!.Id;
                var tracked = await seed.Licenses.SingleAsync(l => l.Id == licenseId);
                tracked.ExpiresAt = manual;
                await seed.SaveChangesAsync();
            }

            var total = 1200m;
            var pay = Task.Run(async () =>
            {
                await using var db = NewContext();
                try
                {
                    await CreateBilling(db).RecordReceiptAsync(staleId, new RecordReceiptRequest
                    { IdempotencyKey = "race-" + round, AmountPaid = total, PaymentMethod = PaymentMethod.Cash }, "admin");
                }
                catch (InvalidOperationException) { } // already voided: payment refused, fine
            });
            var renew = Task.Run(async () =>
            {
                await using var db = NewContext();
                await CreateBilling(db).CreateRenewalInvoiceAsync(licenseId, manual, BillingService.RenewalActor);
            });
            await Task.WhenAll(pay, renew);

            await using var check = NewContext();
            var stale = await check.Invoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == staleId);
            var posted = await check.Receipts.CountAsync(r => r.InvoiceId == staleId && r.Status == ReceiptStatus.Posted);
            Assert.False(stale.Status == InvoiceStatus.Void && posted > 0, $"round {round}: voided an invoice with a posted payment");
        }
    }
}
