using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Helpers;
using Platform.Shared.Dtos.Common;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;
using Platform.Api.Services.Email;

namespace Platform.Api.Services;

public class LicenseService(
    AppDbContext db,
    IBillingService billing,
    IAuditLogService auditLog,
    ILicenseKeyDeliveryService licenseKeyDelivery,
    ILicenseDenyListService denyList,
    IEmailOutboxService outbox,
    EmailTemplateService templates) : ILicenseService
{
    public async Task<LicenseDto> CreateAsync(
        CreateLicenseRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        IDbContextTransaction? transaction = null;
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using var ownedTransaction = transaction;

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
            ExpiresAt = DateTimeNormalizer.ToUtc(request.ExpiresAt),
            PlanName = request.PlanName,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Licenses.Add(license);
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.LicenseIssued, performedBy, request.CustomerId, license.Id,
            null, $$"""{"planName":"{{request.PlanName}}"}""", ipAddress, cancellationToken);

        if (request.CreateInvoice)
        {
            await billing.CreateInvoiceForLicenseAsync(
                license,
                request.InvoiceSubtotal,
                request.InvoiceTaxAmount,
                request.InvoiceCurrency,
                request.InvoiceDueDate,
                $"License {license.PlanName}",
                performedBy,
                request.SendInvoice ? InvoiceStatus.Sent : InvoiceStatus.Draft,
                ipAddress,
                cancellationToken);
        }

        var result = await MapLicenseAsync(license.Id, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Failed to load created license.");
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<LicenseDto?> GetAsync(
        string id,
        bool includeSuspendedCustomers = false,
        CancellationToken cancellationToken = default)
    {
        return await MapLicenseAsync(id, includeSuspendedCustomers, cancellationToken);
    }

    public async Task<PagedResult<LicenseDto>> ListAsync(
        string? customerId = null,
        bool includeSuspendedCustomers = false,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        int? expiringWithinDays = null,
        CancellationToken cancellationToken = default)
    {
        var (normalizedPage, normalizedPageSize, skip) = PagingHelper.Normalize(page, pageSize);

        var query = (includeSuspendedCustomers
                ? db.Licenses.IgnoreQueryFilters()
                : db.Licenses)
            .AsNoTracking()
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .AsQueryable();

        if (customerId is not null)
            query = query.Where(l => l.CustomerId == customerId);

        if (expiringWithinDays is > 0)
        {
            var threshold = DateTime.UtcNow.AddDays(expiringWithinDays.Value);
            query = query.Where(l =>
                l.Status == LicenseStatus.Active
                && l.ExpiresAt.HasValue
                && l.ExpiresAt.Value <= threshold);
        }

        var ordered = query.OrderByDescending(l => l.CreatedAt);

        var totalCount = await ordered.CountAsync(cancellationToken);

        var licenses = await ordered
            .Skip(skip)
            .Take(normalizedPageSize)
            .ToListAsync(cancellationToken);

        var ids = licenses.Select(l => l.Id).ToList();
        var invoiceRows = ids.Count == 0
            ? []
            : await db.Invoices
                .AsNoTracking()
                .Where(i => i.LicenseId != null && ids.Contains(i.LicenseId))
                .Select(i => new { i.LicenseId, i.Id, i.CreatedAt })
                .ToListAsync(cancellationToken);

        var invoiceLookup = invoiceRows
            .GroupBy(i => i.LicenseId!)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.CreatedAt).First().Id);

        return new PagedResult<LicenseDto>
        {
            Items = licenses.Select(l => MapLicense(l, invoiceLookup.GetValueOrDefault(l.Id))).ToList(),
            TotalCount = totalCount,
            Page = normalizedPage,
            PageSize = normalizedPageSize
        };
    }

    public async Task<LicenseDto> ActivateAsync(
        string id,
        ActivateLicenseRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        IDbContextTransaction? transaction = null;
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using var ownedTransaction = transaction;

        var license = await db.Licenses
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("License not found.");

        if (license.Customer.IsSuspended)
            throw new InvalidOperationException("Customer is suspended.");

        if (license.Status is not (LicenseStatus.Pending or LicenseStatus.Suspended or LicenseStatus.Expired))
            throw new InvalidOperationException($"Cannot activate license in status {license.Status}.");

        var now = DateTime.UtcNow;
        license.Status = LicenseStatus.Active;
        license.UpdatedAt = now;

        if (request.EmailLicenseKey)
            await licenseKeyDelivery.DeliverNewKeyAsync(license, isRenewal: false, cancellationToken);
        else if (string.IsNullOrEmpty(license.LicenseKeyHash))
            throw new InvalidOperationException("A new license must be delivered by email when first activated.");

        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.LicenseActivated, performedBy, license.CustomerId, license.Id,
            null, $$"""{"licenseKeyQueued":{{request.EmailLicenseKey.ToString().ToLowerInvariant()}}}""", ipAddress, cancellationToken);

        if (request.CreateInvoice)
        {
            await billing.CreateInvoiceForLicenseAsync(
                license, request.Subtotal, request.TaxAmount, request.Currency,
                request.DueDate, request.Description, performedBy,
                request.SendInvoice ? InvoiceStatus.Sent : InvoiceStatus.Draft,
                ipAddress, cancellationToken);
        }

        var result = await MapLicenseAsync(license.Id, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Failed to load license.");
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<LicenseDto> RenewAsync(
        string id,
        RenewLicenseRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        IDbContextTransaction? transaction = null;
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using var ownedTransaction = transaction;

        var license = await db.Licenses
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("License not found.");

        if (license.Customer.IsSuspended)
            throw new InvalidOperationException("Customer is suspended.");

        if (license.Status is not (LicenseStatus.Active or LicenseStatus.Expired))
            throw new InvalidOperationException($"Cannot renew license in status {license.Status}.");

        var now = DateTime.UtcNow;
        license.Status = LicenseStatus.Active;
        if (request.ExpiresAt.HasValue)
            license.ExpiresAt = DateTimeNormalizer.ToUtc(request.ExpiresAt);
        license.UpdatedAt = now;

        if (request.RotateLicenseKey)
        {
            if (!request.EmailLicenseKey)
                throw new InvalidOperationException("Rotated keys must be emailed to the technical contact.");
            await licenseKeyDelivery.DeliverNewKeyAsync(license, isRenewal: true, cancellationToken);
        }
        else if (request.EmailLicenseKey)
        {
            var renewal = templates.Renewal(license.Customer, license.ServiceProduct, license);
            outbox.Enqueue(
                EmailDeliveryKind.RenewalConfirmation,
                CustomerContactResolver.Technical(license.Customer),
                renewal.Subject,
                renewal.Html,
                license.CustomerId,
                license.Id);
        }
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.LicenseRenewed, performedBy, license.CustomerId, license.Id,
            null, $$"""{"keyRotated":{{request.RotateLicenseKey.ToString().ToLowerInvariant()}}}""", ipAddress, cancellationToken);

        if (request.RotateLicenseKey)
        {
            await auditLog.WriteAsync(AuditAction.LicenseKeyRotated, performedBy, license.CustomerId, license.Id,
                null, null, ipAddress, cancellationToken);
        }

        if (request.CreateInvoice)
        {
            await billing.CreateInvoiceForLicenseAsync(
                license, request.Subtotal, request.TaxAmount, request.Currency,
                request.DueDate, request.Description, performedBy,
                request.SendInvoice ? InvoiceStatus.Sent : InvoiceStatus.Draft,
                ipAddress, cancellationToken);
        }

        var result = await MapLicenseAsync(license.Id, includeSuspendedCustomers: true, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load license.");
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<LicenseDto> UpdateAsync(
        string id,
        UpdateLicenseRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var license = await db.Licenses
            .IgnoreQueryFilters()
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("License not found.");

        if (license.Status is LicenseStatus.Revoked)
            throw new InvalidOperationException("Cannot update a revoked license.");

        license.PlanName = request.PlanName.Trim();
        license.ExpiresAt = DateTimeNormalizer.ToUtc(request.ExpiresAt);
        license.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.LicenseUpdated, performedBy, license.CustomerId, license.Id, null,
            $$"""{"planName":"{{license.PlanName}}","expiresAt":"{{license.ExpiresAt:o}}"}""", ipAddress, cancellationToken);

        return await MapLicenseAsync(license.Id, includeSuspendedCustomers: true, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load license.");
    }

    public async Task<LicenseDto> SuspendAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default,
        string? notificationReason = null)
    {
        var license = await db.Licenses
            .IgnoreQueryFilters()
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("License not found.");

        if (license.Status is not LicenseStatus.Active)
            throw new InvalidOperationException($"Cannot suspend license in status {license.Status}.");

        license.Status = LicenseStatus.Suspended;
        license.UpdatedAt = DateTime.UtcNow;
        var suspended = templates.StatusNotice(
            license.Customer, license.ServiceProduct, EmailDeliveryKind.Suspended, notificationReason);
        outbox.Enqueue(
            EmailDeliveryKind.Suspended,
            CustomerContactResolver.Technical(license.Customer),
            suspended.Subject,
            suspended.Html,
            license.CustomerId,
            license.Id);
        await db.SaveChangesAsync(cancellationToken);

        await denyList.DenyLicenseAsync(license.Id, cancellationToken);

        await auditLog.WriteAsync(AuditAction.LicenseSuspended, performedBy, license.CustomerId, license.Id,
            null, null, ipAddress, cancellationToken);

        return await MapLicenseAsync(license.Id, includeSuspendedCustomers: true, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load license.");
    }

    public async Task<LicenseDto> RevokeAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var license = await db.Licenses
            .IgnoreQueryFilters()
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("License not found.");

        if (license.Status == LicenseStatus.Revoked)
            throw new InvalidOperationException("License is already revoked.");

        license.Status = LicenseStatus.Revoked;
        license.UpdatedAt = DateTime.UtcNow;
        var revoked = templates.StatusNotice(
            license.Customer, license.ServiceProduct, EmailDeliveryKind.Revoked);
        outbox.Enqueue(
            EmailDeliveryKind.Revoked,
            CustomerContactResolver.Technical(license.Customer),
            revoked.Subject,
            revoked.Html,
            license.CustomerId,
            license.Id);
        await db.SaveChangesAsync(cancellationToken);

        await denyList.DenyLicenseAsync(license.Id, cancellationToken);

        await auditLog.WriteAsync(AuditAction.LicenseRevoked, performedBy, license.CustomerId, license.Id,
            null, null, ipAddress, cancellationToken);

        return await MapLicenseAsync(license.Id, includeSuspendedCustomers: true, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load license.");
    }

    public async Task<LicenseDto> ResendKeyAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
        => await RotateKeyAsync(id, performedBy, ipAddress, cancellationToken);

    public async Task<LicenseDto> RotateKeyAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var license = await db.Licenses
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("License not found.");

        if (license.Customer.IsSuspended)
            throw new InvalidOperationException("Customer is suspended.");

        if (license.Status is LicenseStatus.Revoked)
            throw new InvalidOperationException("Cannot rotate the key for a revoked license.");

        await licenseKeyDelivery.DeliverNewKeyAsync(license, isRenewal: true, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.LicenseKeyRotated, performedBy, license.CustomerId, license.Id,
            null, """{"source":"manual-rotation"}""", ipAddress, cancellationToken);

        return await MapLicenseAsync(license.Id, includeSuspendedCustomers: true, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load license.");
    }

    private async Task<LicenseDto?> MapLicenseAsync(
        string id,
        bool includeSuspendedCustomers = false,
        CancellationToken cancellationToken = default)
    {
        var query = includeSuspendedCustomers ? db.Licenses.IgnoreQueryFilters() : db.Licenses;

        var license = await query
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
