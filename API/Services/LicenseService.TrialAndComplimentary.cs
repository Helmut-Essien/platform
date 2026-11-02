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
                .Include(l => l.ServiceProduct)
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
            var invoices = new List<Invoice>();
            if (invoiceIds.Count > 0)
            {
                invoices = await db.Invoices
                    .IgnoreQueryFilters()
                    .Where(i => invoiceIds.Contains(i.Id) && OpenInvoiceStatuses.Contains(i.Status))
                    .ToListAsync(ct);
                foreach (var invoice in invoices)
                {
                    invoice.RenewalForExpiresAt = newEnd;
                    invoice.DueDate = newEnd;
                    // The description (PDF, emails) names the billed period: move it with the trial end.
                    var months = invoice.RenewalPeriodMonths ?? license.BillingPeriodMonths;
                    invoice.Description = BillingService.RenewalDescription(invoice.PlanName ?? license.PlanName,
                        invoice.IsTrialConversion,
                        newEnd, BillingService.RenewalPeriodEnd(newEnd, months, license.BillingAnchorDay));
                    // Keep an already-sent pay link working until the new due date (plus a week).
                    if (invoice.PayLinkExpiresAt is { } linkEnd && linkEnd < newEnd.AddDays(7))
                        invoice.PayLinkExpiresAt = newEnd.AddDays(7);
                    if (invoice.Status == InvoiceStatus.Overdue)
                        invoice.Status = InvoiceStatus.Sent; // no longer past its (new) due date
                    invoice.UpdatedAt = now;
                    moved.Add(invoice.InvoiceNumber);
                }
            }

            // The customer was already told the old end (trial reminder or trial invoice email): tell them the new one.
            var notified = license.TrialReminderForEndsAt is not null
                && await QueueTrialExtendedNoticeAsync(license, invoices.OrderBy(i => i.Id).FirstOrDefault(), now, ct);

            await db.SaveChangesAsync(ct);
            await auditLog.WriteAsync(AuditAction.LicenseTrialExtended, performedBy, license.CustomerId, license.Id, null,
                AuditJson.Serialize(new { previousEndsAt = previous, newEndsAt = newEnd, reopened, invoicesMoved = moved, customerNotified = notified }),
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
            var (voided, keptOpen) = await VoidUnpaidInvoicesAsync(invoiceIds, "license made complimentary", now,
                performedBy, ipAddress, ct);

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

    /// <summary>
    /// Voids the given (already row-locked) open invoices that have no money on them and no checkout in flight,
    /// revoking their pay links. Returns the voided and the kept-open invoice numbers.
    /// </summary>
    private async Task<(List<string> Voided, List<string> KeptOpen)> VoidUnpaidInvoicesAsync(
        IReadOnlyCollection<string> invoiceIds, string reason, DateTime now, string performedBy, string? ipAddress,
        CancellationToken ct)
    {
        var voided = new List<string>();
        var keptOpen = new List<string>();
        if (invoiceIds.Count == 0)
            return (voided, keptOpen);
        var invoices = await db.Invoices
            .IgnoreQueryFilters()
            .Include(i => i.Receipts)
            .Include(i => i.PaymentTransactions)
            .Where(i => invoiceIds.Contains(i.Id) && OpenInvoiceStatuses.Contains(i.Status))
            .ToListAsync(ct);
        // Same rule as a manual void (the void block): a checkout started within the block window may still be
        // paying. With the block switched off (0 minutes) nothing holds the invoice open.
        var blockMinutes = Math.Clamp(_settings.VoidBlockAfterCheckoutMinutes, 0, 24 * 60);
        var since = now.AddMinutes(-blockMinutes);
        var paying = blockMinutes == 0 ? [] : await db.PaymentCheckouts
            .IgnoreQueryFilters()
            .Where(c => invoiceIds.Contains(c.InvoiceId) && c.Status == PaymentCheckoutStatus.Pending && c.CreatedAt >= since)
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
                invoice.Id, AuditJson.Serialize(new { invoiceNumber = invoice.InvoiceNumber, reason }), ipAddress, ct);
        }
        return (voided, keptOpen);
    }

    /// <summary>Renewal lock, then the license's open invoices (trial ones only if asked), in id order. Returns their ids.</summary>
    private async Task<List<string>> LockOpenInvoicesAsync(string licenseId, bool trialOnly, CancellationToken ct)
    {
        await LockRenewalAsync(licenseId, ct);
        var ids = await db.Invoices
            .IgnoreQueryFilters()
            .Where(i => i.LicenseId == licenseId && (!trialOnly || i.IsTrialConversion) && OpenInvoiceStatuses.Contains(i.Status))
            .OrderBy(i => i.Id)
            .Select(i => i.Id)
            .ToListAsync(ct);
        foreach (var invoiceId in ids)
            await LockInvoiceRowAsync(invoiceId, ct);
        return ids;
    }

    /// <summary>
    /// Queues the "trial extended" email in the caller's transaction. Idempotent per new end date: the subject
    /// carries the date, and an existing non-cancelled message for this license with that subject is reused.
    /// </summary>
    private async Task<bool> QueueTrialExtendedNoticeAsync(License license, Invoice? invoice, DateTime now, CancellationToken ct)
    {
        // The invoice is tracked (loaded under lock by the caller): renew a missing/expiring link so the button works.
        var payUrl = invoice is null ? null : payLinks?.EnsureUrl(invoice, now);
        var template = templates.TrialExtended(license.Customer, license.ServiceProduct, license, invoice, payUrl);
        var subject = Email.EmailOutboxService.SanitizeSubject(template.Subject);
        var already = await db.EmailOutboxMessages
            .AnyAsync(m => m.LicenseId == license.Id
                && m.Kind == EmailDeliveryKind.TrialExtended
                && m.Status != EmailDeliveryStatus.Cancelled
                && m.Subject == subject, ct);
        if (already)
            return false;
        outbox.Enqueue(
            EmailDeliveryKind.TrialExtended,
            Email.CustomerContactResolver.Technical(license.Customer),
            template.Subject,
            template.Html,
            license.CustomerId,
            license.Id,
            invoice?.Id);
        return true;
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
