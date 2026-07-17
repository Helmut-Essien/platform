using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Api.Services.Email;
using Platform.Shared.Dtos.Audit;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

/// <summary>
/// PostgreSQL-backed payment ledger checks (locking, unique idempotency, constraints).
/// Skips when Postgres is unavailable.
/// </summary>
public class PaymentLedgerPostgresTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("PLATFORM_TEST_DB")
        ?? "Host=localhost;Port=5432;Database=platform_payment_test;Username=platform;Password=platform_dev";

    [Fact]
    public async Task IdempotentReplay_And_OneReversalConstraint_WorkOnPostgres()
    {
        await using var db = await CreateMigratedContextAsync();
        if (db is null)
            return; // Postgres unavailable

        var customer = new Customer { Name = "Pay Co", ContactEmail = "pay@test.com" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var service = CreateBillingService(db);
        var invoice = await service.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            Status = InvoiceStatus.Sent,
            Currency = "USD",
            Subtotal = 100m,
            TaxAmount = 0m
        }, "admin@test.com");

        var first = await service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "pg-idem-1",
            AmountPaid = 60m,
            PaymentMethod = PaymentMethod.BankTransfer,
            PaymentReference = "PG-REF-1"
        }, "admin@test.com");

        var replay = await service.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "pg-idem-1",
            AmountPaid = 60m,
            PaymentMethod = PaymentMethod.BankTransfer,
            PaymentReference = "PG-REF-1"
        }, "admin@test.com");

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(1, await db.PaymentTransactions.CountAsync(t => t.InvoiceId == invoice.Id));

        await service.ReverseReceiptAsync(invoice.Id, first.Id, new ReverseReceiptRequest
        {
            IdempotencyKey = "pg-rev-1",
            Reason = "duplicate entry"
        }, "admin@test.com");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReverseReceiptAsync(invoice.Id, first.Id, new ReverseReceiptRequest
            {
                IdempotencyKey = "pg-rev-2",
                Reason = "again"
            }, "admin@test.com"));

        var updated = await service.GetInvoiceAsync(invoice.Id);
        Assert.Equal(0m, updated!.AmountPaid);
        Assert.Equal(InvoiceStatus.Sent, updated.Status);
    }

    [Fact]
    public async Task ConcurrentPayments_CannotExceedInvoiceTotal()
    {
        await using var db = await CreateMigratedContextAsync();
        if (db is null)
            return;

        var customer = new Customer { Name = "Race Co", ContactEmail = "race@test.com" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var setup = CreateBillingService(db);
        var invoice = await setup.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            Status = InvoiceStatus.Sent,
            Currency = "USD",
            Subtotal = 100m,
            TaxAmount = 0m
        }, "admin@test.com");

        async Task<(bool Ok, string? Error)> TryPayAsync(string key, decimal amount)
        {
            await using var scoped = await CreateMigratedContextAsync(ensureCreated: false);
            if (scoped is null)
                return (false, "no-db");
            var billing = CreateBillingService(scoped);
            try
            {
                await billing.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
                {
                    IdempotencyKey = key,
                    AmountPaid = amount,
                    PaymentMethod = PaymentMethod.Cash
                }, "admin@test.com");
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        var tasks = Enumerable.Range(0, 4)
            .Select(i => TryPayAsync($"race-{i}-{Guid.NewGuid():N}", 60m))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        var successes = results.Count(r => r.Ok);
        Assert.True(successes <= 1, $"Expected at most one success, got {successes}");

        await using var verify = await CreateMigratedContextAsync(ensureCreated: false);
        Assert.NotNull(verify);
        var net = await verify!.PaymentTransactions
            .Where(t => t.InvoiceId == invoice.Id)
            .GroupBy(_ => 1)
            .Select(g => g.Sum(t => t.Kind == PaymentTransactionKind.Payment ? t.Amount : -t.Amount))
            .FirstOrDefaultAsync();
        Assert.True(net <= 100m);
    }

    private static async Task<AppDbContext?> CreateMigratedContextAsync(bool ensureCreated = true)
    {
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(ConnectionString)
                .Options;
            var db = new AppDbContext(options);
            if (ensureCreated)
                await db.Database.MigrateAsync();
            else if (!await db.Database.CanConnectAsync())
            {
                await db.DisposeAsync();
                return null;
            }

            return db;
        }
        catch
        {
            return null;
        }
    }

    private static BillingService CreateBillingService(AppDbContext db) =>
        new(db, new NoopAudit(), new EmailOutboxService(db), new EmailTemplateService(), new NoopDeny());

    private sealed class NoopAudit : IAuditLogService
    {
        public Task<IReadOnlyList<AuditLogDto>> ListAsync(string? customerId = null, string? licenseId = null, AuditAction? action = null, int limit = 100, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditLogDto>>([]);

        public Task WriteAsync(AuditAction action, string performedBy, string? customerId = null, string? licenseId = null, string? invoiceId = null, string? detailsJson = null, string? ipAddress = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class NoopDeny : ILicenseDenyListService
    {
        public Task DenyLicenseAsync(string licenseId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DenyCustomerLicensesAsync(string customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsDeniedAsync(string licenseId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ClearLicenseDenyAsync(string licenseId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearCustomerDenyAsync(string customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
