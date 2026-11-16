using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Helpers;
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
            .IgnoreQueryFilters()
            .AsNoTracking()
            .GroupBy(l => l.ServiceProductId)
            .Select(g => new { ServiceProductId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ServiceProductId, x => x.Count, cancellationToken);

        var activeKeys = await db.IntegrationKeys
            .AsNoTracking()
            .Where(k => k.IsActive)
            .Select(k => k.ServiceProductId)
            .ToHashSetAsync(cancellationToken);

        var plansByProduct = (await db.ProductPlans
                .AsNoTracking()
                .OrderBy(p => p.SortOrder).ThenBy(p => p.Code)
                .ToListAsync(cancellationToken))
            .ToLookup(p => p.ServiceProductId);

        return products
            .Select(p => MapProduct(p, licenseCounts.GetValueOrDefault(p.Id), activeKeys.Contains(p.Id), plansByProduct[p.Id]))
            .ToList();
    }

    public async Task<ServiceProductDto?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var product = await db.ServiceProducts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
            return null;

        var licenseCount = await db.Licenses.IgnoreQueryFilters().CountAsync(l => l.ServiceProductId == id, cancellationToken);
        var hasKey = await db.IntegrationKeys.AnyAsync(k => k.ServiceProductId == id && k.IsActive, cancellationToken);
        return MapProduct(product, licenseCount, hasKey, await LoadPlansAsync(id, cancellationToken));
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
            IsAvailableForSale = request.IsAvailableForSale,
            DefaultMaxDevices = NormalizeDefaultMaxDevices(request.DefaultMaxDevices)
        };

        db.ServiceProducts.Add(product);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresUniqueViolation.IsUniqueViolation(ex))
        {
            // Two admins creating the same code at once: the unique index wins, report it like the pre-check does.
            throw new InvalidOperationException($"Service product code '{code}' already exists.", ex);
        }

        await auditLog.WriteAsync(AuditAction.ServiceProductCreated, performedBy, null, null, null,
            AuditJson.Serialize(new { serviceProductId = product.Id, code }), ipAddress, cancellationToken);

        return MapProduct(product, 0, false, []);
    }

    public async Task<ServiceProductDto> UpdateAsync(
        string id,
        UpdateServiceProductRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var product = await db.ServiceProducts.FindAsync([id], cancellationToken)
            ?? throw new NotFoundException("Service product not found.");

        product.Name = request.Name.Trim();
        product.Description = request.Description?.Trim();
        product.IsAvailableForSale = request.IsAvailableForSale;
        product.DefaultMaxDevices = NormalizeDefaultMaxDevices(request.DefaultMaxDevices);

        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.ServiceProductUpdated, performedBy, null, null, null,
            AuditJson.Serialize(new { serviceProductId = id, code = product.Code }), ipAddress, cancellationToken);

        var licenseCount = await db.Licenses.IgnoreQueryFilters().CountAsync(l => l.ServiceProductId == id, cancellationToken);
        var hasKey = await db.IntegrationKeys.AnyAsync(k => k.ServiceProductId == id && k.IsActive, cancellationToken);
        return MapProduct(product, licenseCount, hasKey, await LoadPlansAsync(id, cancellationToken));
    }

    public async Task DeleteAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var product = await db.ServiceProducts
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new NotFoundException("Service product not found.");

        var licenseCount = await db.Licenses
            .IgnoreQueryFilters()
            .CountAsync(l => l.ServiceProductId == id, cancellationToken);
        if (licenseCount > 0)
            throw new InvalidOperationException("Cannot delete a service product that has licenses.");

        var hasInvoices = await db.Invoices
            .IgnoreQueryFilters()
            .AnyAsync(i => i.ServiceProductId == id, cancellationToken);
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
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // A license/invoice was created for this product after the checks above (foreign key restrict).
            throw new InvalidOperationException("Cannot delete a service product that was just used by a license or invoice.", ex);
        }

        await auditLog.WriteAsync(AuditAction.ServiceProductDeleted, performedBy, null, null, null,
            AuditJson.Serialize(new { serviceProductId = id, code = product.Code }), ipAddress, cancellationToken);
    }

    private async Task<List<ProductPlan>> LoadPlansAsync(string serviceProductId, CancellationToken cancellationToken) =>
        await db.ProductPlans
            .AsNoTracking()
            .Where(p => p.ServiceProductId == serviceProductId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Code)
            .ToListAsync(cancellationToken);

    /// <summary>Null = server default; anything else is clamped to 0 (unlimited) .. 1000.</summary>
    private static int? NormalizeDefaultMaxDevices(int? value) => value is null ? null : Math.Clamp(value.Value, 0, 1000);

    private static ServiceProductDto MapProduct(
        ServiceProduct product, int licenseCount, bool hasActiveKey, IEnumerable<ProductPlan> plans) => new()
    {
        Id = product.Id,
        Name = product.Name,
        Code = product.Code,
        Description = product.Description,
        IsAvailableForSale = product.IsAvailableForSale,
        HasActiveIntegrationKey = hasActiveKey,
        LicenseCount = licenseCount,
        DefaultMaxDevices = product.DefaultMaxDevices,
        Plans = plans.Select(ProductPlanService.Map).ToList()
    };
}
