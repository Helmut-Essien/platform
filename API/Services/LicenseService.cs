using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public class LicenseService(
    AppDbContext db,
    IBillingService billing,
    IAuditLogService auditLog) : ILicenseService
{
    public async Task<LicenseDto> CreateAsync(
        CreateLicenseRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var customer = await db.Customers.FindAsync([request.CustomerId], cancellationToken)
            ?? throw new InvalidOperationException("Customer not found.");

        if (customer.IsSuspended)
            throw new InvalidOperationException("Cannot issue license for a suspended customer.");

        _ = await db.ServiceProducts.FindAsync([request.ServiceProductId], cancellationToken)
            ?? throw new InvalidOperationException("Service product not found.");

        var now = DateTime.UtcNow;
        var license = new License
        {
            CustomerId = request.CustomerId,
            ServiceProductId = request.ServiceProductId,
            Status = LicenseStatus.Pending,
            ExpiresAt = request.ExpiresAt,
            PlanName = request.PlanName,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Licenses.Add(license);
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.LicenseIssued, performedBy, request.CustomerId, license.Id,
            null, $$"""{"planName":"{{request.PlanName}}"}""", ipAddress, cancellationToken);

        return await MapLicenseAsync(license.Id, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load created license.");
    }

    public async Task<LicenseDto?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        return await MapLicenseAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<LicenseDto>> ListAsync(
        string? customerId = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.Licenses
            .AsNoTracking()
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .AsQueryable();

        if (customerId is not null)
            query = query.Where(l => l.CustomerId == customerId);

        var licenses = await query.OrderByDescending(l => l.CreatedAt).ToListAsync(cancellationToken);

        var ids = licenses.Select(l => l.Id).ToList();
        var invoiceRows = await db.Invoices
            .AsNoTracking()
            .Where(i => i.LicenseId != null && ids.Contains(i.LicenseId))
            .Select(i => new { i.LicenseId, i.Id, i.CreatedAt })
            .ToListAsync(cancellationToken);

        var invoiceLookup = invoiceRows
            .GroupBy(i => i.LicenseId!)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.CreatedAt).First().Id);

        return licenses.Select(l => MapLicense(l, invoiceLookup.GetValueOrDefault(l.Id))).ToList();
    }

    public async Task<LicenseDto> ActivateAsync(
        string id,
        ActivateLicenseRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var license = await db.Licenses
            .Include(l => l.Customer)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("License not found.");

        if (license.Customer.IsSuspended)
            throw new InvalidOperationException("Customer is suspended.");

        if (license.Status is not (LicenseStatus.Pending or LicenseStatus.Suspended or LicenseStatus.Expired))
            throw new InvalidOperationException($"Cannot activate license in status {license.Status}.");

        var now = DateTime.UtcNow;
        license.Status = LicenseStatus.Active;
        license.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.LicenseActivated, performedBy, license.CustomerId, license.Id,
            null, null, ipAddress, cancellationToken);

        await billing.CreateInvoiceForLicenseAsync(license, request.Subtotal, request.TaxAmount, request.Currency,
            request.DueDate, request.Description, performedBy, InvoiceStatus.Sent, ipAddress, cancellationToken);

        return await MapLicenseAsync(license.Id, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load license.");
    }

    public async Task<LicenseDto> RenewAsync(
        string id,
        RenewLicenseRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var license = await db.Licenses
            .Include(l => l.Customer)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("License not found.");

        if (license.Customer.IsSuspended)
            throw new InvalidOperationException("Customer is suspended.");

        if (license.Status is not (LicenseStatus.Active or LicenseStatus.Expired))
            throw new InvalidOperationException($"Cannot renew license in status {license.Status}.");

        var now = DateTime.UtcNow;
        license.Status = LicenseStatus.Active;
        if (request.ExpiresAt.HasValue)
            license.ExpiresAt = request.ExpiresAt;
        license.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.LicenseRenewed, performedBy, license.CustomerId, license.Id,
            null, null, ipAddress, cancellationToken);

        await billing.CreateInvoiceForLicenseAsync(license, request.Subtotal, request.TaxAmount, request.Currency,
            request.DueDate, request.Description, performedBy, InvoiceStatus.Sent, ipAddress, cancellationToken);

        return await MapLicenseAsync(license.Id, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load license.");
    }

    private async Task<LicenseDto?> MapLicenseAsync(string id, CancellationToken cancellationToken)
    {
        var license = await db.Licenses
            .AsNoTracking()
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken);

        if (license is null)
            return null;

        var latestInvoiceId = await db.Invoices
            .AsNoTracking()
            .Where(i => i.LicenseId == id)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => i.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return MapLicense(license, latestInvoiceId);
    }

    private static LicenseDto MapLicense(License license, string? latestInvoiceId = null) => new()
    {
        Id = license.Id,
        CustomerId = license.CustomerId,
        CustomerName = license.Customer?.Name,
        ServiceProductId = license.ServiceProductId,
        ServiceProductCode = license.ServiceProduct?.Code,
        Status = license.Status,
        ExpiresAt = license.ExpiresAt,
        PlanName = license.PlanName,
        LicenseKeySentAt = license.LicenseKeySentAt,
        CreatedAt = license.CreatedAt,
        UpdatedAt = license.UpdatedAt,
        LatestInvoiceId = latestInvoiceId
    };
}
