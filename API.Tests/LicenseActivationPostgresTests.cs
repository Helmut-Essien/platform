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
public class LicenseActivationPostgresTests
{
    private static readonly string ConnectionString = new Npgsql.NpgsqlConnectionStringBuilder(
        Environment.GetEnvironmentVariable("PLATFORM_TEST_DB")
        ?? "Host=localhost;Port=5432;Database=platform_payment_test;Username=platform;Password=platform_dev")
    {
        Database = "platform_activation_test"
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
            return false;
        }
    }

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
}
