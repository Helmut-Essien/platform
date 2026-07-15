using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Shared.Dtos.ServiceProducts;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public class ServiceProductService(AppDbContext db, IAuditLogService auditLog) : IServiceProductService
{
    public async Task<IReadOnlyList<ServiceProductDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var products = await db.ServiceProducts
            .AsNoTracking()
            .OrderBy(p => p.Code)
            .ToListAsync(cancellationToken);

        var licenseCounts = await db.Licenses
            .AsNoTracking()
            .GroupBy(l => l.ServiceProductId)
            .Select(g => new { ServiceProductId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ServiceProductId, x => x.Count, cancellationToken);

        var activeKeys = await db.IntegrationKeys
            .AsNoTracking()
            .Where(k => k.IsActive)
            .Select(k => k.ServiceProductId)
            .ToHashSetAsync(cancellationToken);

        return products.Select(p => MapProduct(p, licenseCounts.GetValueOrDefault(p.Id), activeKeys.Contains(p.Id))).ToList();
    }

    public async Task<ServiceProductDto?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var product = await db.ServiceProducts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
            return null;

        var licenseCount = await db.Licenses.CountAsync(l => l.ServiceProductId == id, cancellationToken);
        var hasKey = await db.IntegrationKeys.AnyAsync(k => k.ServiceProductId == id && k.IsActive, cancellationToken);
        return MapProduct(product, licenseCount, hasKey);
    }

    public async Task<ServiceProductDto> CreateAsync(
        CreateServiceProductRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var code = request.Code.Trim().ToUpperInvariant();

        if (await db.ServiceProducts.AnyAsync(p => p.Code == code, cancellationToken))
            throw new InvalidOperationException($"Service product code '{code}' already exists.");

        var product = new ServiceProduct
        {
            Name = request.Name.Trim(),
            Code = code,
            Description = request.Description?.Trim(),
            IsAvailableForSale = request.IsAvailableForSale
        };

        db.ServiceProducts.Add(product);
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.ServiceProductCreated, performedBy, null, null, null,
            $$"""{"serviceProductId":"{{product.Id}}","code":"{{code}}"}""", ipAddress, cancellationToken);

        return MapProduct(product, 0, false);
    }

    public async Task<ServiceProductDto> UpdateAsync(
        string id,
        UpdateServiceProductRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var product = await db.ServiceProducts.FindAsync([id], cancellationToken)
            ?? throw new InvalidOperationException("Service product not found.");

        product.Name = request.Name.Trim();
        product.Description = request.Description?.Trim();
        product.IsAvailableForSale = request.IsAvailableForSale;

        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.ServiceProductUpdated, performedBy, null, null, null,
            $$"""{"serviceProductId":"{{id}}","code":"{{product.Code}}"}""", ipAddress, cancellationToken);

        var licenseCount = await db.Licenses.CountAsync(l => l.ServiceProductId == id, cancellationToken);
        var hasKey = await db.IntegrationKeys.AnyAsync(k => k.ServiceProductId == id && k.IsActive, cancellationToken);
        return MapProduct(product, licenseCount, hasKey);
    }

    public async Task DeleteAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var product = await db.ServiceProducts
            .Include(p => p.Licenses)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Service product not found.");

        if (product.Licenses.Count > 0)
            throw new InvalidOperationException("Cannot delete a service product that has active licenses.");

        var hasInvoices = await db.Invoices.AnyAsync(i => i.ServiceProductId == id, cancellationToken);
        if (hasInvoices)
            throw new InvalidOperationException("Cannot delete a service product that has associated invoices.");

        var hasActiveIntegrationKeys = await db.IntegrationKeys.AnyAsync(k => k.ServiceProductId == id && k.IsActive, cancellationToken);
        if (hasActiveIntegrationKeys)
            throw new InvalidOperationException("Cannot delete a service product that has active integration keys.");

        var revokedKeys = await db.IntegrationKeys
            .Where(k => k.ServiceProductId == id && !k.IsActive)
            .ToListAsync(cancellationToken);
        db.IntegrationKeys.RemoveRange(revokedKeys);

        db.ServiceProducts.Remove(product);
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.ServiceProductDeleted, performedBy, null, null, null,
            $$"""{"serviceProductId":"{{id}}","code":"{{product.Code}}"}""", ipAddress, cancellationToken);
    }

    private static ServiceProductDto MapProduct(ServiceProduct product, int licenseCount, bool hasActiveKey) => new()
    {
        Id = product.Id,
        Name = product.Name,
        Code = product.Code,
        Description = product.Description,
        IsAvailableForSale = product.IsAvailableForSale,
        HasActiveIntegrationKey = hasActiveKey,
        LicenseCount = licenseCount
    };
}
