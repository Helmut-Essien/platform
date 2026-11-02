using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Helpers;
using Platform.Api.Services.Renewals;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

/// <summary>
/// Free trials and complimentary (free until changed) licenses. Lock order matches billing:
/// per-license renewal advisory lock, then invoice rows (id order), then the license row.
/// </summary>
public partial class LicenseService
{
    private static readonly InvoiceStatus[] OpenInvoiceStatuses =
        [InvoiceStatus.Draft, InvoiceStatus.Sent, InvoiceStatus.Overdue, InvoiceStatus.PartiallyPaid];

    public Task<LicenseDto> ExtendTrialAsync(
        string id,
        ExtendTrialRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default) =>
        RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
            await LockRenewalAsync(id, ct);
            // The unpaid trial invoice (if any) moves with the trial end, so paying it still converts the trial.
            var invoiceIds = await db.Invoices
                .IgnoreQueryFilters()
                .Where(i => i.LicenseId == id && i.IsTrialConversion && OpenInvoiceStatuses.Contains(i.Status))
                .OrderBy(i => i.Id)
                .Select(i => i.Id)
                .ToListAsync(ct);
            foreach (var invoiceId in invoiceIds)
                await LockInvoiceRowAsync(invoiceId, ct);
            await LockLicenseRowAsync(id, ct);

            var license = await db.Licenses
                .IgnoreQueryFilters()
                .Include(l => l.Customer)
                .FirstOrDefaultAsync(l => l.Id == id, ct)
                ?? throw new NotFoundException("License not found.");

            if (!license.IsTrial)
                throw new InvalidOperationException("This license is not on a free trial.");
            if (license.Customer.IsSuspended)
                throw new InvalidOperationException("Customer is suspended.");
            if (license.Status is not (LicenseStatus.Active or LicenseStatus.Expired))
                throw new InvalidOperationException($"Cannot extend a trial in status {license.Status}.");

            var now = DateTime.UtcNow;
            var newEnd = DateTimeNormalizer.ToUtc(request.TrialEndsAt)
                ?? throw new InvalidOperationException("New trial end date is required.");
            if (DateTimeNormalizer.IsPastExpiry(newEnd, now))
                throw new InvalidOperationException("New trial end must be in the future.");
            if (license.ExpiresAt is { } current && newEnd <= current)
                throw new InvalidOperationException($"New trial end must be after the current one ({current:dd MMM yyyy}).");
            var cap = TrialCap(license.TrialStartedAt ?? license.CreatedAt, TrialMaxMonths)!.Value;
            if (newEnd > cap)
                throw new InvalidOperationException(
                    $"A trial can run for at most {TrialMaxMonths} month(s): the latest end date is {cap:dd MMM yyyy}.");

            var previous = license.ExpiresAt;
            license.ExpiresAt = newEnd;
            SetAnchor(license);
            license.UpdatedAt = now;
            var reopened = license.Status == LicenseStatus.Expired;
            if (reopened)
            {
                license.Status = LicenseStatus.Active;
                license.AutoSuspendedForOverdueInvoiceId = null;
            }

            var moved = new List<string>();
            if (invoiceIds.Count > 0)
            {
                var invoices = await db.Invoices
                    .IgnoreQueryFilters()
                    .Where(i => invoiceIds.Contains(i.Id) && OpenInvoiceStatuses.Contains(i.Status))
                    .ToListAsync(ct);
                foreach (var invoice in invoices)
                {
                    invoice.RenewalForExpiresAt = newEnd;
                    invoice.DueDate = newEnd;
                    if (invoice.Status == InvoiceStatus.Overdue)
                        invoice.Status = InvoiceStatus.Sent; // no longer past its (new) due date
                    invoice.UpdatedAt = now;
                    moved.Add(invoice.InvoiceNumber);
                }
            }

            await db.SaveChangesAsync(ct);
            await auditLog.WriteAsync(AuditAction.LicenseTrialExtended, performedBy, license.CustomerId, license.Id, null,
                AuditJson.Serialize(new { previousEndsAt = previous, newEndsAt = newEnd, reopened, invoicesMoved = moved }),
                ipAddress, ct);

            var result = await MapLicenseAsync(license.Id, includeSuspendedCustomers: true, ct)
                ?? throw new InvalidOperationException("Failed to load license.");
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            await AfterAccessChangeAsync(license, ct);
            return result;
        }, cancellationToken);

    public Task<LicenseDto> MakeComplimentaryAsync(
        string id,
        MakeComplimentaryRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default) =>
        RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
            await LockRenewalAsync(id, ct); // the renewal worker can't invoice it mid-switch
            var invoiceIds = await db.Invoices
                .IgnoreQueryFilters()
                .Where(i => i.LicenseId == id && OpenInvoiceStatuses.Contains(i.Status))
                .OrderBy(i => i.Id)
                .Select(i => i.Id)
                .ToListAsync(ct);
            foreach (var invoiceId in invoiceIds)
                await LockInvoiceRowAsync(invoiceId, ct);
            await LockLicenseRowAsync(id, ct);

            var license = await db.Licenses
                .IgnoreQueryFilters()
                .Include(l => l.Customer)
                .Include(l => l.ServiceProduct)
                .FirstOrDefaultAsync(l => l.Id == id, ct)
                ?? throw new NotFoundException("License not found.");

            if (license.Status == LicenseStatus.Revoked)
                throw new InvalidOperationException("Cannot change a revoked license.");
            if (license.Customer.IsSuspended)
                throw new InvalidOperationException("Customer is suspended.");

            var now = DateTime.UtcNow;
            var expiry = DateTimeNormalizer.ToUtc(request.ExpiresAt);
            if (DateTimeNormalizer.IsPastExpiry(expiry, now))
                throw new InvalidOperationException("Expiry must be in the future (or empty for no expiry).");

            // Unpaid invoices are no longer owed. Ones with money on them, or a customer mid-payment, stay for the
            // admin to settle (refund or void) and are listed in the audit entry.
            var voided = new List<string>();
            var keptOpen = new List<string>();
            if (invoiceIds.Count > 0)
            {
                var invoices = await db.Invoices
                    .IgnoreQueryFilters()
                    .Include(i => i.Receipts)
                    .Include(i => i.PaymentTransactions)
                    .Where(i => invoiceIds.Contains(i.Id) && OpenInvoiceStatuses.Contains(i.Status))
                    .ToListAsync(ct);
                var paying = await db.PaymentCheckouts
                    .IgnoreQueryFilters()
                    .Where(c => invoiceIds.Contains(c.InvoiceId) && c.Status == PaymentCheckoutStatus.Pending)
                    .Select(c => c.InvoiceId)
                    .Distinct()
                    .ToListAsync(ct);
                foreach (var invoice in invoices)
                {
                    if (paying.Contains(invoice.Id)
                        || BillingService.GetNetPaid(invoice) != 0
                        || invoice.Receipts.Any(r => r.Status == ReceiptStatus.Posted))
                    {
                        keptOpen.Add(invoice.InvoiceNumber);
                        continue;
                    }
                    invoice.Status = InvoiceStatus.Void;
                    invoice.UpdatedAt = now;
                    PayLinkService.Revoke(invoice);
                    voided.Add(invoice.InvoiceNumber);
                    await auditLog.WriteAsync(AuditAction.InvoiceVoided, performedBy, invoice.CustomerId, invoice.LicenseId,
                        invoice.Id, AuditJson.Serialize(new { invoiceNumber = invoice.InvoiceNumber, reason = "license made complimentary" }),
                        ipAddress, ct);
                }
            }

            var previousStatus = license.Status;
            var wasTrial = license.IsTrial;
            MakeFree(license);
            license.ExpiresAt = expiry;
            SetAnchor(license);
            license.UpdatedAt = now;

            // Free access starts now. A manual suspension is the admin's call and stays; one caused by an overdue
            // invoice no longer applies.
            var deliverKey = string.IsNullOrEmpty(license.LicenseKeyHash);
            var activate = license.Status is LicenseStatus.Pending or LicenseStatus.Expired
                || (license.Status == LicenseStatus.Suspended && license.AutoSuspendedForOverdueInvoiceId is not null);
            if (activate)
                await LicenseActivation.ActivateAsync(license, licenseKeyDelivery, deliverKey, now, ct);

            await db.SaveChangesAsync(ct);
            await auditLog.WriteAsync(AuditAction.LicenseMadeComplimentary, performedBy, license.CustomerId, license.Id, null,
                AuditJson.Serialize(new
                {
                    expiresAt = expiry,
                    previousStatus = previousStatus.ToString(),
                    status = license.Status.ToString(),
                    endedTrial = wasTrial,
                    invoicesVoided = voided,
                    invoicesLeftOpen = keptOpen
                }), ipAddress, ct);

            var result = await MapLicenseAsync(license.Id, includeSuspendedCustomers: true, ct)
                ?? throw new InvalidOperationException("Failed to load license.");
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            await AfterAccessChangeAsync(license, ct);
            return result;
        }, cancellationToken);

    public async Task<LicenseDto> EndComplimentaryAsync(
        string id,
        EndComplimentaryRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.Billing?.Amount is not > 0)
            throw new InvalidOperationException("Set a price above zero to switch this license to paid.");

        var expiry = await RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
            await LockRenewalAsync(id, ct);
            await LockLicenseRowAsync(id, ct);
            var license = await db.Licenses
                .IgnoreQueryFilters()
                .Include(l => l.Customer)
                .FirstOrDefaultAsync(l => l.Id == id, ct)
                ?? throw new NotFoundException("License not found.");

            if (!license.IsComplimentary)
                throw new InvalidOperationException("This license is not complimentary.");
            if (license.Status == LicenseStatus.Revoked)
                throw new InvalidOperationException("Cannot change a revoked license.");

            var now = DateTime.UtcNow;
            var newExpiry = DateTimeNormalizer.ToUtc(request.ExpiresAt)
                ?? (license.ExpiresAt is { } e && !DateTimeNormalizer.IsPastExpiry(e, now)
                    ? e
                    : DateTime.SpecifyKind(now.Date.AddDays(14), DateTimeKind.Utc));
            if (DateTimeNormalizer.IsPastExpiry(newExpiry, now))
                throw new InvalidOperationException("Billing start date must be in the future.");

            license.IsComplimentary = false;
            ApplyBilling(license, request.Billing, _defaultCurrency);
            var previous = license.ExpiresAt;
            license.ExpiresAt = newExpiry;
            SetAnchor(license);
            license.UpdatedAt = now;
            await db.SaveChangesAsync(ct);

            await auditLog.WriteAsync(AuditAction.LicenseComplimentaryEnded, performedBy, license.CustomerId, license.Id, null,
                AuditJson.Serialize(new
                {
                    price = license.BillingAmount,
                    tax = license.BillingTaxAmount,
                    currency = license.BillingCurrency,
                    periodMonths = license.BillingPeriodMonths,
                    previousExpiresAt = previous,
                    freeUntil = newExpiry,
                    invoiceNow = request.CreateInvoiceNow
                }), ipAddress, ct);

            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return newExpiry;
        }, cancellationToken);

        await denyList.ClearLicenseDenyAsync(id, cancellationToken);

        // The first paid period starts when the free one ends; its invoice is a renewal invoice for that date, so the
        // worker (or this call) creates it exactly once and paying it extends the license by one period.
        if (request.CreateInvoiceNow)
        {
            db.ChangeTracker.Clear();
            try
            {
                await billing.CreateRenewalInvoiceAsync(id, expiry, performedBy, explicitRequest: true,
                    ipAddress: ipAddress, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ConflictException)
            {
                // The switch is saved; the renewal scan invoices it before the billing start date instead.
            }
        }

        db.ChangeTracker.Clear();
        return await MapLicenseAsync(id, includeSuspendedCustomers: true, cancellationToken)
            ?? throw new InvalidOperationException("Failed to load license.");
    }

    private async Task LockRenewalAsync(string licenseId, CancellationToken ct)
    {
        if (db.Database.IsRelational())
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({BillingService.RenewalLockClass}, hashtext({licenseId}))", ct);
    }

    private async Task LockInvoiceRowAsync(string invoiceId, CancellationToken ct)
    {
        if (db.Database.IsRelational())
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "Invoices" WHERE "Id" = {invoiceId} FOR UPDATE""", ct);
    }

    /// <summary>Validation cache and deny list follow the committed status.</summary>
    private async Task AfterAccessChangeAsync(License license, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(license.LicenseKeyLookupHash))
            await denyList.InvalidateValidationCacheAsync(license.ServiceProductId, license.LicenseKeyLookupHash, ct);
        if (license.Status == LicenseStatus.Active)
            await denyList.ClearLicenseDenyAsync(license.Id, ct);
    }
}
