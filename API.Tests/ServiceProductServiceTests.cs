using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Shared.Dtos.Audit;
using Platform.Shared.Dtos.ServiceProducts;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class ServiceProductServiceTests
{
    [Fact]
    public async Task DeleteAsync_DeletesProductWhenNoLicensesExist()
    {
        await using var db = CreateDbContext();
        var product = await SeedServiceProductAsync(db);
        var auditLog = new FakeAuditLogService();
        var service = new ServiceProductService(db, auditLog);

        await service.DeleteAsync(product.Id, performedBy: "admin@example.com");

        Assert.False(await db.ServiceProducts.AnyAsync(p => p.Id == product.Id));
        Assert.Contains(auditLog.Entries, e =>
            e.Action == AuditAction.ServiceProductDeleted &&
            e.DetailsJson!.Contains(product.Id));
    }

    [Fact]
    public async Task DeleteAsync_ThrowsWhenProductNotFound()
    {
        await using var db = CreateDbContext();
        var service = new ServiceProductService(db, new FakeAuditLogService());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync("nonexistent-id", performedBy: "admin@example.com"));

        Assert.Equal("Service product not found.", ex.Message);
    }

    [Fact]
    public async Task DeleteAsync_ThrowsWhenProductHasLicenses()
    {
        await using var db = CreateDbContext();
        var product = await SeedServiceProductAsync(db);
        var customer = new Customer
        {
            Name = "Acme Ltd",
            ContactEmail = "billing@acme.test"
        };
        var license = new License
        {
            Customer = customer,
            CustomerId = customer.Id,
            ServiceProduct = product,
            ServiceProductId = product.Id,
            Status = LicenseStatus.Active,
            PlanName = "Pro",
            LicenseKeyHash = "hash-placeholder"
        };
        db.AddRange(customer, license);
        await db.SaveChangesAsync();

        var service = new ServiceProductService(db, new FakeAuditLogService());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(product.Id, performedBy: "admin@example.com"));

        Assert.Equal("Cannot delete a service product that has active licenses.", ex.Message);
    }

    [Fact]
    public async Task DeleteAsync_ThrowsWhenProductHasInvoices()
    {
        await using var db = CreateDbContext();
        var product = await SeedServiceProductAsync(db);
        var customer = new Customer
        {
            Name = "Acme Ltd",
            ContactEmail = "billing@acme.test"
        };
        var invoice = new Invoice
        {
            Customer = customer,
            CustomerId = customer.Id,
            ServiceProduct = product,
            ServiceProductId = product.Id,
            InvoiceNumber = "INV-2026-00001",
            Currency = "USD",
            Status = InvoiceStatus.Sent
        };
        db.AddRange(customer, invoice);
        await db.SaveChangesAsync();

        var service = new ServiceProductService(db, new FakeAuditLogService());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(product.Id, performedBy: "admin@example.com"));

        Assert.Equal("Cannot delete a service product that has associated invoices.", ex.Message);
    }

    [Fact]
    public async Task DeleteAsync_ThrowsWhenProductHasActiveIntegrationKey()
    {
        await using var db = CreateDbContext();
        var product = await SeedServiceProductAsync(db);
        var key = new IntegrationKey
        {
            ServiceProduct = product,
            ServiceProductId = product.Id,
            KeyHash = BCrypt.Net.BCrypt.HashPassword("secret-abc"),
            IsActive = true
        };
        db.IntegrationKeys.Add(key);
        await db.SaveChangesAsync();

        var service = new ServiceProductService(db, new FakeAuditLogService());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(product.Id, performedBy: "admin@example.com"));

        Assert.Equal("Cannot delete a service product that has active integration keys.", ex.Message);
    }

    [Fact]
    public async Task DeleteAsync_AllowsDeletionWhenAllKeysAreRevoked()
    {
        await using var db = CreateDbContext();
        var product = await SeedServiceProductAsync(db);
        var key = new IntegrationKey
        {
            ServiceProduct = product,
            ServiceProductId = product.Id,
            KeyHash = BCrypt.Net.BCrypt.HashPassword("secret-abc"),
            IsActive = false
        };
        db.IntegrationKeys.Add(key);
        await db.SaveChangesAsync();

        var auditLog = new FakeAuditLogService();
        var service = new ServiceProductService(db, auditLog);

        await service.DeleteAsync(product.Id, performedBy: "admin@example.com");

        Assert.False(await db.ServiceProducts.AnyAsync(p => p.Id == product.Id));
        Assert.False(await db.IntegrationKeys.AnyAsync(k => k.Id == key.Id));
        Assert.Contains(auditLog.Entries, e =>
            e.Action == AuditAction.ServiceProductDeleted);
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    private static async Task<ServiceProduct> SeedServiceProductAsync(AppDbContext db)
    {
        var product = new ServiceProduct
        {
            Name = "Test Product",
            Code = "TEST01"
        };

        db.ServiceProducts.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    private sealed class FakeAuditLogService : IAuditLogService
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task<IReadOnlyList<AuditLogDto>> ListAsync(
            string? customerId = null,
            string? licenseId = null,
            AuditAction? action = null,
            int limit = 100,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<AuditLogDto>>([]);
        }

        public Task WriteAsync(
            AuditAction action,
            string performedBy,
            string? customerId = null,
            string? licenseId = null,
            string? invoiceId = null,
            string? detailsJson = null,
            string? ipAddress = null,
            CancellationToken cancellationToken = default)
        {
            Entries.Add(new AuditEntry(action, performedBy, customerId, licenseId, invoiceId, detailsJson, ipAddress));
            return Task.CompletedTask;
        }
    }

    private sealed record AuditEntry(
        AuditAction Action,
        string PerformedBy,
        string? CustomerId,
        string? LicenseId,
        string? InvoiceId,
        string? DetailsJson,
        string? IpAddress);
}
