using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Enums;
using Xunit;
using static API.Tests.LicenseActivationTests;

namespace API.Tests;

/// <summary>Auto-activation under real row locks and the activation partial unique index. Skips without Postgres.</summary>
public class LicenseActivationPostgresTests : IAsyncLifetime
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
    public async Task ConcurrentWebhookAndReconcile_ActivateExactlyOnce()
    {
        if (!await MigrateAsync())
            return;
        string licenseId, invoiceId;
        await using (var seed = NewContext())
        {
            var (license, invoice) = await IssuePricedAsync(Create(seed));
            licenseId = license.Id;
            invoiceId = invoice.Id;
        }

        // Same Paystack reference arriving through webhook + reconcile + manual retries at once.
        await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            await using var db = NewContext();
            try
            {
                await Create(db).Billing.RecordReceiptAsync(invoiceId, Pay(1200m, "paystack:race"), $"actor{i}");
            }
            catch (ConflictException)
            {
            }
        }));

        await using var check = NewContext();
        var stored = await check.Licenses.IgnoreQueryFilters().FirstAsync(l => l.Id == licenseId);
        Assert.Equal(LicenseStatus.Active, stored.Status);
        Assert.Equal(1, await check.Receipts.CountAsync(r => r.InvoiceId == invoiceId));
        Assert.Equal(1, await check.EmailOutboxMessages.CountAsync(m => m.Kind == EmailDeliveryKind.LicenseKey && m.LicenseId == licenseId));
    }

    [Fact]
    public async Task UniqueIndex_RejectsSecondLiveActivationInvoice()
    {
        if (!await MigrateAsync())
            return;
        await using var db = NewContext();
        var (license, invoice) = await IssuePricedAsync(Create(db));
        db.Invoices.Add(new Invoice
        {
            CustomerId = license.CustomerId,
            LicenseId = license.Id,
            InvoiceNumber = "INV-ACT-" + NUlid.Ulid.NewUlid().ToString()[^8..],
            Currency = "GHS",
            Subtotal = 1200m,
            TotalAmount = 1200m,
            Status = InvoiceStatus.Sent,
            IsActivationInvoice = true
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        _ = invoice;
    }

    [Fact]
    public async Task ManualActivate_RacingFinalPayment_NoDeadlock_OneKeyEmail()
    {
        if (!await MigrateAsync())
            return;
        for (var round = 0; round < 8; round++)
        {
            string licenseId, invoiceId;
            await using (var seed = NewContext())
            {
                var (license, invoice) = await IssuePricedAsync(Create(seed));
                licenseId = license.Id;
                invoiceId = invoice.Id;
            }

            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var activate = Task.Run(async () =>
            {
                await using var db = NewContext();
                try
                {
                    await Create(db).Licenses.ActivateAsync(licenseId,
                        new Platform.Shared.Dtos.Licenses.ActivateLicenseRequest { CreateInvoice = false }, "admin");
                }
                catch (InvalidOperationException) { } // payment won and activated it first
                catch (Exception ex) { errors.Add(ex); }
            });
            var pay = Task.Run(async () =>
            {
                await using var db = NewContext();
                try { await Create(db).Billing.RecordReceiptAsync(invoiceId, Pay(1200m, "paystack:r" + round), "system"); }
                catch (Exception ex) { errors.Add(ex); }
            });
            await Task.WhenAll(activate, pay);

            Assert.Empty(errors); // a deadlock (40P01) would surface here
            await using var check = NewContext();
            Assert.Equal(LicenseStatus.Active, (await check.Licenses.IgnoreQueryFilters().SingleAsync(l => l.Id == licenseId)).Status);
            Assert.Equal(1, await check.EmailOutboxMessages.CountAsync(m => m.Kind == EmailDeliveryKind.LicenseKey && m.LicenseId == licenseId));
        }
    }
}
