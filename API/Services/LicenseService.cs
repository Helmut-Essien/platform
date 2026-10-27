using Microsoft.EntityFrameworkCore;
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
    public Task<LicenseDto> CreateAsync(
        CreateLicenseRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default) =>
        RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
        var customer = await db.Customers.FindAsync([request.CustomerId], ct)
            ?? throw new NotFoundException("Customer not found.");

        if (customer.IsSuspended)
            throw new InvalidOperationException("Cannot issue license for a suspended customer.");

        _ = await db.ServiceProducts.FindAsync([request.ServiceProductId], ct)
            ?? throw new NotFoundException("Service product not found.");

        var openLicenseExists = await db.Licenses
            .IgnoreQueryFilters()
            .AnyAsync(l => l.CustomerId == request.CustomerId
                && l.ServiceProductId == request.ServiceProductId
                && l.Status != LicenseStatus.Revoked, ct);
        if (openLicenseExists)
            throw new InvalidOperationException(
                "This customer already has a license for that service. Renew, activate, or revoke it before issuing another.");

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
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresUniqueViolation.IsUniqueViolation(ex))
        {
            throw new InvalidOperationException(
                "This customer already has a license for that service. Renew, activate, or revoke it before issuing another.",
                ex);
        }

        await auditLog.WriteAsync(AuditAction.LicenseIssued, performedBy, request.CustomerId, license.Id,
            null, AuditJson.Serialize(new { planName = request.PlanName }), ipAddress, ct);

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
                ct);
        }

        var result = await MapLicenseAsync(license.Id, cancellationToken: ct)
            ?? throw new InvalidOperationException("Failed to load created license.");
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return result;
        }, cancellationToken);

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
        string? serviceProductId = null,
        LicenseStatus? status = null,
        string? search = null,
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

        if (!string.IsNullOrEmpty(serviceProductId))
            query = query.Where(l => l.ServiceProductId == serviceProductId);

        if (status is not null)
            query = query.Where(l => l.Status == status);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(l =>
                l.Customer.Name.ToLower().Contains(term)
                || l.PlanName.ToLower().Contains(term)
                || (l.ServiceProduct != null && l.ServiceProduct.Code.ToLower().Contains(term)));
        }

        // Id tiebreaker keeps paging stable for licenses created in the same instant.
        var ordered = query.OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id);

        var totalCount = await ordered.CountAsync(cancellationToken);

        var licenses = await ordered
            .Skip(skip)
            .Take(normalizedPageSize)
            .ToListAsync(cancellationToken);

        var ids = licenses.Select(l => l.Id).ToList();
        var invoiceQuery = includeSuspendedCustomers
            ? db.Invoices.IgnoreQueryFilters()
            : db.Invoices;
        var invoiceRows = ids.Count == 0
            ? []
            : await invoiceQuery
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

    public Task<LicenseDto> ActivateAsync(
        string id,
        ActivateLicenseRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default) =>
        RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
        var license = await db.Licenses
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new NotFoundException("License not found.");

        if (license.Customer.IsSuspended)
            throw new InvalidOperationException("Customer is suspended.");

        if (license.Status is not (LicenseStatus.Pending or LicenseStatus.Suspended or LicenseStatus.Expired))
            throw new InvalidOperationException($"Cannot activate license in status {license.Status}.");

        var now = DateTime.UtcNow;
        if (DateTimeNormalizer.IsPastExpiry(license.ExpiresAt, now))
            throw new InvalidOperationException("License expiry is in the past. Renew the license before activating.");

        // B4: a license that already has a key (Suspended/Expired) is being restored, not issued.
        // The request flags default to "email key + create invoice", so they are ignored here;
        // use Rotate Key / Renew for those actions explicitly.
        var restoring = license.Status != LicenseStatus.Pending && !string.IsNullOrEmpty(license.LicenseKeyHash);
        var emailKey = request.EmailLicenseKey && !restoring;
        var createInvoice = request.CreateInvoice && !restoring;

        license.Status = LicenseStatus.Active;
        license.AutoSuspendedForOverdueInvoiceId = null;
        license.UpdatedAt = now;

        if (emailKey)
            await licenseKeyDelivery.DeliverNewKeyAsync(license, isRenewal: false, ct);
        else if (string.IsNullOrEmpty(license.LicenseKeyHash))
            throw new InvalidOperationException("A new license must be delivered by email when first activated.");

        await db.SaveChangesAsync(ct);

        await auditLog.WriteAsync(AuditAction.LicenseActivated, performedBy, license.CustomerId, license.Id,
            null, AuditJson.Serialize(new { licenseKeyQueued = emailKey, restored = restoring }), ipAddress, ct);

        if (createInvoice)
        {
            await billing.CreateInvoiceForLicenseAsync(
                license, request.Subtotal, request.TaxAmount, request.Currency,
                request.DueDate, request.Description, performedBy,
                request.SendInvoice ? InvoiceStatus.Sent : InvoiceStatus.Draft,
                ipAddress, ct);
        }

        var result = await MapLicenseAsync(license.Id, cancellationToken: ct)
            ?? throw new InvalidOperationException("Failed to load license.");
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        await denyList.ClearLicenseDenyAsync(license.Id, ct);
        return result;
        }, cancellationToken);

    public Task<LicenseDto> ReactivateAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default) =>
        RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
            var license = await db.Licenses
                .Include(l => l.Customer)
                .Include(l => l.ServiceProduct)
                .FirstOrDefaultAsync(l => l.Id == id, ct)
                ?? throw new NotFoundException("License not found.");

            if (license.Customer.IsSuspended)
                throw new InvalidOperationException("Customer is suspended.");

            if (license.Status != LicenseStatus.Suspended)
                throw new InvalidOperationException($"Only suspended licenses can be reactivated (current status {license.Status}).");

            var now = DateTime.UtcNow;
            if (DateTimeNormalizer.IsPastExpiry(license.ExpiresAt, now))
                throw new InvalidOperationException("License expiry is in the past. Renew the license first.");

            license.Status = LicenseStatus.Active;
            license.AutoSuspendedForOverdueInvoiceId = null;
            license.UpdatedAt = now;
            await db.SaveChangesAsync(ct);

            await auditLog.WriteAsync(AuditAction.LicenseReactivated, performedBy, license.CustomerId, license.Id,
                null, null, ipAddress, ct);

            var result = await MapLicenseAsync(license.Id, includeSuspendedCustomers: true, ct)
                ?? throw new InvalidOperationException("Failed to load license.");
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            await denyList.ClearLicenseDenyAsync(license.Id, ct);
            return result;
        }, cancellationToken);

    public Task<LicenseDto> RenewAsync(
        string id,
        RenewLicenseRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default) =>
        RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
        var license = await db.Licenses
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new NotFoundException("License not found.");

        if (license.Customer.IsSuspended)
            throw new InvalidOperationException("Customer is suspended.");

        if (license.Status is not (LicenseStatus.Active or LicenseStatus.Expired or LicenseStatus.Suspended))
            throw new InvalidOperationException($"Cannot renew license in status {license.Status}.");

        var now = DateTime.UtcNow;
        var renewedExpiry = DateTimeNormalizer.ToUtc(request.ExpiresAt);
        if (!renewedExpiry.HasValue || DateTimeNormalizer.IsPastExpiry(renewedExpiry, now))
            throw new InvalidOperationException("Renewal expiry must be in the future.");

        // B8: renewing extends the term; it must not silently lift an admin/overdue suspension.
        // Suspended licenses stay Suspended until explicitly reactivated.
        var stillSuspended = license.Status == LicenseStatus.Suspended;
        if (!stillSuspended)
        {
            license.Status = LicenseStatus.Active;
            license.AutoSuspendedForOverdueInvoiceId = null;
        }
        license.ExpiresAt = renewedExpiry;
        license.UpdatedAt = now;

        if (request.RotateLicenseKey)
        {
            if (!request.EmailLicenseKey)
                throw new InvalidOperationException("Rotated keys must be emailed to the technical contact.");
            await licenseKeyDelivery.DeliverNewKeyAsync(license, isRenewal: true, ct);
        }
        else if (request.EmailLicenseKey && !stillSuspended)
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
        await db.SaveChangesAsync(ct);

        await auditLog.WriteAsync(AuditAction.LicenseRenewed, performedBy, license.CustomerId, license.Id,
            null, AuditJson.Serialize(new { keyRotated = request.RotateLicenseKey }), ipAddress, ct);

        if (request.RotateLicenseKey)
        {
            await auditLog.WriteAsync(AuditAction.LicenseKeyRotated, performedBy, license.CustomerId, license.Id,
                null, null, ipAddress, ct);
        }

        if (request.CreateInvoice)
        {
            await billing.CreateInvoiceForLicenseAsync(
                license, request.Subtotal, request.TaxAmount, request.Currency,
                request.DueDate, request.Description, performedBy,
                request.SendInvoice ? InvoiceStatus.Sent : InvoiceStatus.Draft,
                ipAddress, ct);
        }

        var result = await MapLicenseAsync(license.Id, includeSuspendedCustomers: true, ct)
            ?? throw new InvalidOperationException("Failed to load license.");
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        if (!stillSuspended)
            await denyList.ClearLicenseDenyAsync(license.Id, ct);
        return result;
        }, cancellationToken);

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
            ?? throw new NotFoundException("License not found.");

        if (license.Status is LicenseStatus.Revoked)
            throw new InvalidOperationException("Cannot update a revoked license.");

        license.PlanName = request.PlanName.Trim();
        license.ExpiresAt = DateTimeNormalizer.ToUtc(request.ExpiresAt);
        license.UpdatedAt = DateTime.UtcNow;
        var becameExpired = license.Status == LicenseStatus.Active
            && DateTimeNormalizer.IsPastExpiry(license.ExpiresAt, license.UpdatedAt);
        if (becameExpired)
            license.Status = LicenseStatus.Expired;

        if (!string.IsNullOrEmpty(license.LicenseKeyLookupHash))
        {
            await denyList.InvalidateValidationCacheAsync(
                license.ServiceProductId,
                license.LicenseKeyLookupHash,
                cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);

        if (becameExpired)
            await denyList.DenyLicenseAsync(license.Id, cancellationToken);

        await auditLog.WriteAsync(AuditAction.LicenseUpdated, performedBy, license.CustomerId, license.Id, null,
            AuditJson.Serialize(new { planName = license.PlanName, expiresAt = license.ExpiresAt, status = license.Status.ToString() }),
            ipAddress, cancellationToken);

        return await MapLicenseAsync(license.Id, includeSuspendedCustomers: true, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load license.");
    }

    public async Task<LicenseDto> SuspendAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default,
        string? notificationReason = null,
        string? autoSuspendedForOverdueInvoiceId = null)
    {
        var license = await db.Licenses
            .IgnoreQueryFilters()
            .Include(l => l.Customer)
            .Include(l => l.ServiceProduct)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new NotFoundException("License not found.");

        if (license.Status is not LicenseStatus.Active)
            throw new InvalidOperationException($"Cannot suspend license in status {license.Status}.");

        license.Status = LicenseStatus.Suspended;
        license.UpdatedAt = DateTime.UtcNow;
        license.AutoSuspendedForOverdueInvoiceId = string.IsNullOrWhiteSpace(autoSuspendedForOverdueInvoiceId)
            ? null
            : autoSuspendedForOverdueInvoiceId;
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
            ?? throw new NotFoundException("License not found.");

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
            ?? throw new NotFoundException("License not found.");

        if (license.Customer.IsSuspended)
            throw new InvalidOperationException("Customer is suspended.");

        if (license.Status is LicenseStatus.Revoked)
            throw new InvalidOperationException("Cannot rotate the key for a revoked license.");

        await licenseKeyDelivery.DeliverNewKeyAsync(license, isRenewal: true, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        await auditLog.WriteAsync(AuditAction.LicenseKeyRotated, performedBy, license.CustomerId, license.Id,
            null, AuditJson.Serialize(new { source = "manual-rotation" }), ipAddress, cancellationToken);

        return await MapLicenseAsync(license.Id, includeSuspendedCustomers: true, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load license.");
    }

    private const int ExpiryBatchSize = 200;
    private const int DenyListBatchSize = 50;

    public async Task<int> ExpireDueLicensesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var horizon = now.Date.AddDays(1);

        // P7: one query for everything that is due (instead of an id query plus a reload per license).
        var due = await db.Licenses
            .IgnoreQueryFilters()
            .Where(l => l.Status == LicenseStatus.Active
                && l.ExpiresAt != null
                && l.ExpiresAt < horizon)
            .ToListAsync(cancellationToken);

        var dueIds = due
            .Where(l => DateTimeNormalizer.IsPastExpiry(l.ExpiresAt, now))
            .Select(l => l.Id)
            .ToList();

        var expired = 0;
        var first = true;
        foreach (var chunk in dueIds.Chunk(ExpiryBatchSize))
        {
            if (!first)
            {
                // Later chunks are reloaded fresh (the tracker is cleared after each chunk).
                due = await db.Licenses
                    .IgnoreQueryFilters()
                    .Where(l => chunk.Contains(l.Id))
                    .ToListAsync(cancellationToken);
            }

            first = false;
            var batch = due.Where(l => chunk.Contains(l.Id)).ToList();
            expired += await ExpireBatchAsync(batch, chunk, now, cancellationToken);
            db.ChangeTracker.Clear();
        }

        return expired;
    }

    /// <summary>
    /// Expires a batch with one save (statuses and audit entries together), then updates the deny list in
    /// bounded concurrent groups. If another writer changed one of the licenses in the meantime (the
    /// <c>Version</c> concurrency token), falls back to the original one-at-a-time re-check for that batch.
    /// </summary>
    private async Task<int> ExpireBatchAsync(
        List<License> batch,
        string[] ids,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var expiring = batch
            .Where(l => l.Status == LicenseStatus.Active && DateTimeNormalizer.IsPastExpiry(l.ExpiresAt, now))
            .ToList();
        if (expiring.Count == 0)
            return 0;

        try
        {
            var audits = new List<AuditEntryRequest>(expiring.Count);
            foreach (var license in expiring)
            {
                license.Status = LicenseStatus.Expired;
                license.UpdatedAt = now;
                audits.Add(new AuditEntryRequest(
                    AuditAction.LicenseExpired,
                    "system:license-expiry",
                    license.CustomerId,
                    license.Id,
                    null,
                    AuditJson.Serialize(new { expiresAt = license.ExpiresAt })));
            }

            await auditLog.WriteBatchAsync(audits, cancellationToken);
            await db.SaveChangesAsync(cancellationToken); // no-op when the audit service already saved
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var count = 0;
            foreach (var id in ids)
                count += await ExpireSingleAsync(id, now, cancellationToken);
            return count;
        }

        await DenyAllAsync(expiring.Select(l => l.Id), cancellationToken);
        return expiring.Count;
    }

    private async Task<int> ExpireSingleAsync(string id, DateTime now, CancellationToken cancellationToken)
    {
        var license = await db.Licenses
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (license is null
            || license.Status != LicenseStatus.Active
            || !DateTimeNormalizer.IsPastExpiry(license.ExpiresAt, now))
        {
            return 0;
        }

        license.Status = LicenseStatus.Expired;
        license.UpdatedAt = now;
        await auditLog.WriteBatchAsync(
        [
            new AuditEntryRequest(
                AuditAction.LicenseExpired,
                "system:license-expiry",
                license.CustomerId,
                license.Id,
                null,
                AuditJson.Serialize(new { expiresAt = license.ExpiresAt }))
        ], cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await denyList.DenyLicenseAsync(license.Id, cancellationToken);
        db.ChangeTracker.Clear();
        return 1;
    }

    private async Task DenyAllAsync(IEnumerable<string> licenseIds, CancellationToken cancellationToken)
    {
        foreach (var group in licenseIds.Chunk(DenyListBatchSize))
            await Task.WhenAll(group.Select(id => denyList.DenyLicenseAsync(id, cancellationToken)));
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

        var invoiceQuery = includeSuspendedCustomers
            ? db.Invoices.IgnoreQueryFilters()
            : db.Invoices;
        var latestInvoiceId = await invoiceQuery
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
