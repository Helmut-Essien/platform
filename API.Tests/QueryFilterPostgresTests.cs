using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Api.Services.Email;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

/// <summary>
/// Verifies admin queries see suspended-customer billing data when IgnoreQueryFilters is applied.
/// Skips when Postgres is unavailable.
/// </summary>
public class QueryFilterPostgresTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("PLATFORM_TEST_DB")
        ?? "Host=localhost;Port=5432;Database=platform_query_filter_test;Username=platform;Password=platform_dev";

    [Fact]
    public async Task ListInvoices_IncludesSuspendedCustomerInvoices()
    {
        await using var db = await CreateMigratedContextAsync();
        if (db is null)
            return;

        var customer = new Customer
        {
            Name = "Suspended Co",
            ContactEmail = "suspended@test.com",
            IsSuspended = true
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        db.Invoices.Add(new Invoice
        {
            CustomerId = customer.Id,
            InvoiceNumber = "INV-QF-001",
            Status = InvoiceStatus.Sent,
            IssueDate = DateTime.UtcNow.Date,
            Currency = "USD",
            Subtotal = 50m,
            TaxAmount = 0m,
            TotalAmount = 50m
        });
        await db.SaveChangesAsync();

        var filteredCount = await db.Invoices.CountAsync();
        Assert.Equal(0, filteredCount);

        var billing = CreateBillingService(db);
        var listed = await billing.ListInvoicesAsync(customerId: customer.Id);

        Assert.Equal(1, listed.TotalCount);
        Assert.Equal("INV-QF-001", listed.Items[0].InvoiceNumber);
    }

    private static BillingService CreateBillingService(AppDbContext db)
    {
        var outbox = new EmailOutboxService(db);
        var templates = new EmailTemplateService();
        return new BillingService(
            db,
            new AuditLogService(db),
            outbox,
            templates,
            new FakeDenyList());
    }

    private sealed class FakeDenyList : ILicenseDenyListService
    {
        public Task ClearCustomerDenyAsync(string customerId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ClearLicenseDenyAsync(string licenseId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DenyCustomerLicensesAsync(string customerId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DenyLicenseAsync(string licenseId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<bool> IsDeniedAsync(string licenseId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private static async Task<AppDbContext?> CreateMigratedContextAsync()
    {
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(ConnectionString)
                .Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return db;
        }
        catch
        {
            return null;
        }
    }
}
