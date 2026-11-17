using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Helpers;
using Platform.Api.Services.Email;
using Platform.Shared.Dtos.ServiceProducts;
using Platform.Shared.Enums;

namespace Platform.Api.Services.PriceChanges;

/// <summary>
/// Scheduled price changes for a plan's licenses. The license stays the source of truth: a pending change waits for
/// the first renewal invoice whose period starts on or after its effective date (see
/// <c>BillingService.CreateRenewalInvoiceAsync</c>), which then bills the new price and copies it onto the license.
/// Invoices already issued never change.
/// </summary>
public class PlanPriceChangeService(
    AppDbContext db,
    IAuditLogService auditLog,
    IEmailOutboxService outbox,
    EmailTemplateService templates,
    IOptions<BillingSettings>? billingSettings = null) : IPlanPriceChangeService
{
    internal const int MaxPreviewRows = 2000;
    internal const int MaxScheduleLicenses = 2000;
    internal const int MaxYearsAhead = 5;
    internal const int NoticeBatchSize = 200;
    private const int MaxNoticeBatchesPerRun = 10;
    internal const string NoticeActor = "system:price-change-notice";

    private readonly BillingSettings _settings = billingSettings?.Value ?? new BillingSettings();

    private int NoticeDays => NoticeDaysOf(_settings);

    /// <summary>Billing:PriceChangeNoticeDays clamped to 0–365 (0 = no notice emails).</summary>
    internal static int NoticeDaysOf(BillingSettings settings) => Math.Clamp(settings.PriceChangeNoticeDays, 0, 365);

    private sealed record NewPrice(decimal Amount, decimal Tax, string Currency, int? PeriodMonths, DateTime EffectiveFrom);

    private NewPrice Normalize(PlanPriceChangeRequest request, DateTime now)
    {
        if (request.Amount <= 0)
            throw new InvalidOperationException("New price must be above zero. To stop billing a license, edit it instead.");
        BillingService.EnsureValidMoney(request.Amount, "New price");
        BillingService.EnsureValidMoney(request.TaxAmount, "Tax");
        var currency = string.IsNullOrWhiteSpace(request.Currency)
            ? BillingService.DefaultCurrencyOf(_settings)
            : BillingService.NormalizeCurrency(request.Currency);
        if (request.PeriodMonths is < 1 or > 36)
            throw new InvalidOperationException("Billing period must be 1 to 36 months.");
        // A calendar date: the day part only, so a time zone shift in the client can't move it.
        var effective = DateTime.SpecifyKind(request.EffectiveFrom.Date, DateTimeKind.Utc);
        var today = DateTime.SpecifyKind(now.Date, DateTimeKind.Utc);
        if (effective < today)
            throw new InvalidOperationException("The effective date can't be in the past.");
        if (effective > today.AddYears(MaxYearsAhead))
            throw new InvalidOperationException($"The effective date must be within {MaxYearsAhead} years.");
        return new NewPrice(request.Amount, request.TaxAmount, currency, request.PeriodMonths, effective);
    }

    private bool NoticeNowFor(DateTime effective, DateTime now) =>
        NoticeDays > 0 && effective <= DateTime.SpecifyKind(now.Date, DateTimeKind.Utc).AddDays(NoticeDays);

    /// <summary>Why a license can't take the change, or null when it can.</summary>
    private static string? Ineligibility(License license, string planCode, NewPrice price, LicensePriceChangeDto? pending)
    {
        if (license.Status == LicenseStatus.Revoked)
            return "Revoked";
        if (!string.Equals(license.PlanName, planCode, StringComparison.Ordinal))
            return "No longer on this plan";
        if (license.IsComplimentary)
            return "Complimentary (free)";
        if (license.BillingAmount is not > 0 || string.IsNullOrEmpty(license.BillingCurrency))
            return "No price on this license (it isn't invoiced automatically)";
        if (license.BillingAmount == price.Amount && license.BillingTaxAmount == price.Tax
            && string.Equals(license.BillingCurrency, price.Currency, StringComparison.Ordinal)
            && license.BillingPeriodMonths == (price.PeriodMonths ?? license.BillingPeriodMonths))
            return "Already at this price";
        // The same change again (a double submit, or the next page of a large plan): nothing to do, no second email.
        if (pending is not null && pending.EffectiveFrom == price.EffectiveFrom && pending.NewAmount == price.Amount
            && pending.NewTaxAmount == price.Tax && string.Equals(pending.NewCurrency, price.Currency, StringComparison.Ordinal)
            && pending.NewPeriodMonths == price.PeriodMonths && string.Equals(pending.PlanCode, planCode, StringComparison.Ordinal))
            return "Already scheduled at this price and date";
        return null;
    }

    private async Task<(ServiceProduct Product, ProductPlan Plan)> LoadPlanAsync(
        string serviceProductId, string planId, bool tracked, CancellationToken cancellationToken)
    {
        var product = await db.ServiceProducts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == serviceProductId, cancellationToken)
            ?? throw new NotFoundException("Service product not found.");
        var plans = tracked ? db.ProductPlans : db.ProductPlans.AsNoTracking();
        var plan = await plans.FirstOrDefaultAsync(p => p.Id == planId && p.ServiceProductId == serviceProductId, cancellationToken)
            ?? throw new NotFoundException("Plan not found.");
        return (product, plan);
    }

    public async Task<PlanPriceChangePreviewDto> PreviewAsync(
        string serviceProductId, string planId, PlanPriceChangeRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var price = Normalize(request, now);
        var (_, plan) = await LoadPlanAsync(serviceProductId, planId, tracked: false, cancellationToken);

        var onPlan = db.Licenses.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.ServiceProductId == serviceProductId && l.PlanName == plan.Code);
        var revoked = await onPlan.CountAsync(l => l.Status == LicenseStatus.Revoked, cancellationToken);
        var licenses = await onPlan
            .Where(l => l.Status != LicenseStatus.Revoked)
            .Include(l => l.Customer)
            // Licenses already scheduled at exactly this price and date go last, so on a plan with more than
            // MaxPreviewRows licenses the next preview after confirming shows the ones still to do.
            .OrderBy(l => db.LicensePriceChanges.IgnoreQueryFilters().Any(c => c.LicenseId == l.Id
                && c.Status == LicensePriceChangeStatus.Pending && c.EffectiveFrom == price.EffectiveFrom
                && c.NewAmount == price.Amount && c.NewTaxAmount == price.Tax && c.NewCurrency == price.Currency
                && c.NewPeriodMonths == price.PeriodMonths))
            .ThenBy(l => l.Customer.Name).ThenBy(l => l.Id)
            .Take(MaxPreviewRows + 1)
            .ToListAsync(cancellationToken);
        var truncated = licenses.Count > MaxPreviewRows;
        if (truncated)
            licenses.RemoveAt(licenses.Count - 1);

        var ids = licenses.Select(l => l.Id).ToList();
        var pending = await LicensePriceChangeOps.LoadPendingAsync(db, ids, cancellationToken);
        // Renewal invoices already issued for a period starting on/after the effective date keep the old price.
        var issued = ids.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await db.Invoices.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.LicenseId != null && ids.Contains(i.LicenseId)
                    && i.RenewalForExpiresAt != null && i.RenewalForExpiresAt >= price.EffectiveFrom
                    && i.Status != InvoiceStatus.Void)
                .Select(i => i.LicenseId!)
                .Distinct()
                .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

        var rows = licenses.Select(l =>
        {
            var existing = pending.GetValueOrDefault(l.Id);
            var reason = Ineligibility(l, plan.Code, price, existing);
            return new PlanPriceChangePreviewRowDto
            {
                LicenseId = l.Id,
                CustomerId = l.CustomerId,
                CustomerName = l.Customer?.Name,
                CustomerSuspended = l.Customer?.IsSuspended ?? false,
                Status = l.Status,
                IsTrial = l.IsTrial,
                IsComplimentary = l.IsComplimentary,
                ExpiresAt = l.ExpiresAt,
                CurrentAmount = l.BillingAmount,
                CurrentTaxAmount = l.BillingTaxAmount,
                CurrentCurrency = l.BillingCurrency,
                CurrentPeriodMonths = l.BillingPeriodMonths,
                NewPeriodMonths = price.PeriodMonths ?? l.BillingPeriodMonths,
                Eligible = reason is null,
                IneligibleReason = reason,
                ExistingPendingEffectiveFrom = existing?.EffectiveFrom,
                NextInvoiceAlreadyIssued = issued.Contains(l.Id)
            };
        }).ToList();

        return new PlanPriceChangePreviewDto
        {
            PlanCode = plan.Code,
            PlanDisplayName = plan.DisplayName,
            EffectiveFrom = price.EffectiveFrom,
            NewAmount = price.Amount,
            NewTaxAmount = price.Tax,
            NewCurrency = price.Currency,
            NewPeriodMonths = price.PeriodMonths,
            NoticeDays = NoticeDays,
            NoticeSentOnConfirm = NoticeNowFor(price.EffectiveFrom, now),
            RevokedCount = revoked,
            Truncated = truncated,
            Rows = rows
        };
    }

    public async Task<SchedulePlanPriceChangeResultDto> ScheduleAsync(
        string serviceProductId,
        string planId,
        SchedulePlanPriceChangeRequest request,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var price = Normalize(request, now);
        var submitted = (request.LicenseIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        // License ids are ULIDs: anything longer can't match, so it is skipped without reaching the database.
        var requested = submitted.Where(id => id.Length <= 26).ToList();
        if (submitted.Count == 0 && !request.UpdatePlanDefaultPrice)
            throw new InvalidOperationException("Choose at least one license (or only update the plan's default price).");
        if (submitted.Count > MaxScheduleLicenses)
            throw new InvalidOperationException($"At most {MaxScheduleLicenses} licenses can be changed at once.");
        await LoadPlanAsync(serviceProductId, planId, tracked: false, cancellationToken); // 404s before any locking

        return await RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
            // Lock order: plan rows, then license rows, then price-change rows. Exclusive when the plan row itself is
            // updated (a shared lock upgraded later could deadlock with a license edit holding the shared lock).
            await ProductPlanRules.LockPlansAsync(db, serviceProductId, exclusive: request.UpdatePlanDefaultPrice, ct);
            var (product, plan) = await LoadPlanAsync(serviceProductId, planId, tracked: true, ct);
            await LicensePriceChangeOps.LockLicenseRowsAsync(db, requested, ct);

            var licenses = requested.Count == 0
                ? []
                : await db.Licenses.IgnoreQueryFilters()
                    .Include(l => l.Customer)
                    .Where(l => requested.Contains(l.Id) && l.ServiceProductId == serviceProductId)
                    .OrderBy(l => l.Id)
                    .ToListAsync(ct);
            var pendingNow = await LicensePriceChangeOps.LoadPendingAsync(db, licenses.Select(l => l.Id).ToList(), ct);
            var eligible = licenses.Where(l => Ineligibility(l, plan.Code, price, pendingNow.GetValueOrDefault(l.Id)) is null).ToList();

            // Replace earlier pending changes first, in their own save: the one-pending-per-license index must
            // never see the old and the new row pending together.
            var eligibleIds = eligible.Select(l => l.Id).ToList();
            var announced = eligibleIds.Where(id => pendingNow.GetValueOrDefault(id)?.NoticeQueuedAt is not null)
                .ToHashSet(StringComparer.Ordinal);
            // No "cancelled" email for a replaced change: the customer gets the new notice instead.
            var replaced = await LicensePriceChangeOps.CancelPendingAsync(db, auditLog, outbox, templates, eligible,
                LicensePriceChangeOps.ReasonReplaced, performedBy, ipAddress, notifyCustomer: false, ct);
            await db.SaveChangesAsync(ct);

            var batchId = NUlid.Ulid.NewUlid().ToString();
            var noticeNow = NoticeNowFor(price.EffectiveFrom, now);
            var invoiced = NoticeDays > 0
                ? await NextInvoiceIssuedAsync(eligible.Select(l => (l, price.EffectiveFrom)).ToList(), ct)
                : new HashSet<string>(StringComparer.Ordinal);
            var planLabel = plan.DisplayName;
            var audits = new List<AuditEntryRequest>(eligible.Count * 2 + 2);
            var notices = 0;
            foreach (var license in eligible)
            {
                var change = new LicensePriceChange
                {
                    LicenseId = license.Id,
                    BatchId = batchId,
                    PlanCode = plan.Code,
                    EffectiveFrom = price.EffectiveFrom,
                    OldAmount = license.BillingAmount,
                    OldTaxAmount = license.BillingTaxAmount,
                    OldCurrency = license.BillingCurrency,
                    OldPeriodMonths = license.BillingPeriodMonths,
                    NewAmount = price.Amount,
                    NewTaxAmount = price.Tax,
                    NewCurrency = price.Currency,
                    NewPeriodMonths = price.PeriodMonths,
                    CreatedAt = now,
                    CreatedBy = performedBy
                };
                db.LicensePriceChanges.Add(change);
                audits.Add(new AuditEntryRequest(AuditAction.LicensePriceChangeScheduled, performedBy, license.CustomerId,
                    license.Id, null, AuditJson.Serialize(new
                    {
                        priceChangeId = change.Id,
                        batchId,
                        plan = plan.Code,
                        effectiveFrom = price.EffectiveFrom,
                        from = new { amount = license.BillingAmount, tax = license.BillingTaxAmount, currency = license.BillingCurrency, periodMonths = license.BillingPeriodMonths },
                        to = new { amount = price.Amount, tax = price.Tax, currency = price.Currency, periodMonths = price.PeriodMonths ?? license.BillingPeriodMonths }
                    }), ipAddress));

                // Inside the notice window, or replacing a change the customer was already told about: tell them now.
                // A suspended customer is told when reinstated (the worker skips them until then).
                if (NoticeDays > 0 && (noticeNow || announced.Contains(license.Id)) && !license.Customer.IsSuspended)
                {
                    QueueNotice(license, product, change, planLabel, now, invoiced.Contains(license.Id));
                    audits.Add(NoticeAudit(license, change, performedBy, ipAddress));
                    notices++;
                }
            }

            var defaultUpdated = false;
            if (request.UpdatePlanDefaultPrice)
            {
                plan.DefaultAmount = price.Amount;
                plan.DefaultTaxAmount = price.Tax;
                plan.DefaultCurrency = price.Currency;
                plan.DefaultPeriodMonths = price.PeriodMonths ?? plan.DefaultPeriodMonths ?? 12;
                plan.UpdatedAt = now;
                defaultUpdated = true;
                audits.Add(new AuditEntryRequest(AuditAction.ProductPlanUpdated, performedBy, null, null, null,
                    AuditJson.Serialize(new
                    {
                        serviceProductId,
                        productCode = product.Code,
                        code = plan.Code,
                        defaultPrice = new { amount = plan.DefaultAmount, tax = plan.DefaultTaxAmount, currency = plan.DefaultCurrency, periodMonths = plan.DefaultPeriodMonths },
                        source = "price_change"
                    }), ipAddress));
            }

            audits.Add(new AuditEntryRequest(AuditAction.PlanPriceChangeScheduled, performedBy, null, null, null,
                AuditJson.Serialize(new
                {
                    batchId,
                    serviceProductId,
                    productCode = product.Code,
                    plan = plan.Code,
                    effectiveFrom = price.EffectiveFrom,
                    amount = price.Amount,
                    tax = price.Tax,
                    currency = price.Currency,
                    periodMonths = price.PeriodMonths,
                    scheduled = eligible.Count,
                    replaced,
                    skipped = submitted.Count - eligible.Count,
                    noticesQueued = notices,
                    planDefaultUpdated = defaultUpdated
                }), ipAddress));

            await auditLog.WriteBatchAsync(audits, ct); // one save: changes, notices, plan, audit rows
            await db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return new SchedulePlanPriceChangeResultDto
            {
                ScheduledCount = eligible.Count,
                ReplacedCount = replaced,
                SkippedCount = submitted.Count - eligible.Count,
                NoticesQueuedCount = notices,
                PlanDefaultPriceUpdated = defaultUpdated
            };
        }, cancellationToken);
    }

    private void QueueNotice(License license, ServiceProduct product, LicensePriceChange change, string? planLabel, DateTime now,
        bool nextInvoiceAlreadyIssued)
    {
        var mail = templates.PriceChangeNotice(license.Customer, product, license, change, planLabel, nextInvoiceAlreadyIssued);
        outbox.Enqueue(EmailDeliveryKind.PriceChangeNotice, CustomerContactResolver.Billing(license.Customer),
            mail.Subject, mail.Html, license.CustomerId, license.Id);
        change.NoticeQueuedAt = now;
    }

    /// <summary>
    /// Licenses whose renewal invoice for the current expiry (a period starting on/after the effective date) is already
    /// issued: that invoice keeps the old price. One query for the batch.
    /// </summary>
    private async Task<HashSet<string>> NextInvoiceIssuedAsync(IReadOnlyCollection<(License License, DateTime EffectiveFrom)> items,
        CancellationToken cancellationToken)
    {
        var candidates = items.Where(i => i.License.ExpiresAt is { } exp && exp >= i.EffectiveFrom).Select(i => i.License).ToList();
        if (candidates.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);
        var ids = candidates.Select(l => l.Id).ToList();
        var from = candidates.Min(l => l.ExpiresAt!.Value);
        var rows = await db.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.LicenseId != null && ids.Contains(i.LicenseId) && i.RenewalForExpiresAt != null
                && i.RenewalForExpiresAt >= from && i.Status != InvoiceStatus.Void)
            .Select(i => new { LicenseId = i.LicenseId!, Period = i.RenewalForExpiresAt!.Value })
            .ToListAsync(cancellationToken);
        var expiry = candidates.ToDictionary(l => l.Id, l => l.ExpiresAt!.Value, StringComparer.Ordinal);
        return rows.Where(r => expiry[r.LicenseId] == r.Period).Select(r => r.LicenseId).ToHashSet(StringComparer.Ordinal);
    }

    private static AuditEntryRequest NoticeAudit(License license, LicensePriceChange change, string performedBy, string? ipAddress) =>
        new(AuditAction.LicensePriceChangeNoticeQueued, performedBy, license.CustomerId, license.Id, null,
            AuditJson.Serialize(new { priceChangeId = change.Id, effectiveFrom = change.EffectiveFrom }), ipAddress);

    public async Task<IReadOnlyList<LicensePriceChangeDto>> ListForLicenseAsync(string licenseId, CancellationToken cancellationToken = default)
    {
        if (!await db.Licenses.IgnoreQueryFilters().AnyAsync(l => l.Id == licenseId, cancellationToken))
            throw new NotFoundException("License not found.");
        var rows = await db.LicensePriceChanges.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.LicenseId == licenseId)
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .Take(50)
            .ToListAsync(cancellationToken);
        return rows.Select(LicensePriceChangeOps.Map).ToList();
    }

    public Task<LicensePriceChangeDto> CancelAsync(
        string licenseId, string priceChangeId, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default) =>
        RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
            await LicensePriceChangeOps.LockLicenseRowsAsync(db, [licenseId], ct);
            var license = await db.Licenses.IgnoreQueryFilters().FirstOrDefaultAsync(l => l.Id == licenseId, ct)
                ?? throw new NotFoundException("License not found.");
            var change = await db.LicensePriceChanges.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == priceChangeId && c.LicenseId == licenseId, ct)
                ?? throw new NotFoundException("Price change not found.");
            if (change.Status != LicensePriceChangeStatus.Pending)
                throw new InvalidOperationException(change.Status == LicensePriceChangeStatus.Applied
                    ? "This price change has already been applied to a renewal invoice. Edit the license to change its price."
                    : "This price change is already cancelled.");

            await LicensePriceChangeOps.CancelPendingAsync(db, auditLog, outbox, templates, [license],
                LicensePriceChangeOps.ReasonCancelled, performedBy, ipAddress, notifyCustomer: true, ct,
                only: c => c.Id == priceChangeId);
            await db.SaveChangesAsync(ct);
            var saved = await db.LicensePriceChanges.IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.Id == priceChangeId, ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return LicensePriceChangeOps.Map(saved);
        }, cancellationToken);

    /// <summary>
    /// Pending changes whose notice is due (effective date within the notice window) and not yet queued. Sent once:
    /// the change row's concurrency token stops two overlapping runs (or a concurrent cancel) both acting on it.
    /// Uses IX_LicensePriceChanges_NoticeDue.
    /// </summary>
    public async Task<int> QueueDueNoticesAsync(DateTime utcNow, CancellationToken cancellationToken = default)
    {
        if (NoticeDays <= 0)
            return 0;
        var horizon = DateTime.SpecifyKind(utcNow.Date, DateTimeKind.Utc).AddDays(NoticeDays);
        var queued = 0;
        var skip = new HashSet<string>(StringComparer.Ordinal);
        for (var batch = 0; batch < MaxNoticeBatchesPerRun; batch++)
        {
            var due = await db.LicensePriceChanges.IgnoreQueryFilters()
                .Include(c => c.License).ThenInclude(l => l.Customer)
                .Include(c => c.License).ThenInclude(l => l.ServiceProduct)
                .Where(c => c.Status == LicensePriceChangeStatus.Pending
                    && c.NoticeQueuedAt == null
                    && c.EffectiveFrom <= horizon
                    && !c.License.Customer.IsSuspended
                    && !skip.Contains(c.Id))
                .OrderBy(c => c.EffectiveFrom).ThenBy(c => c.Id)
                .Take(NoticeBatchSize)
                .ToListAsync(cancellationToken);
            if (due.Count == 0)
                break;

            var planNames = await ProductPlanDisplay.LoadAsync(db, due.Select(c => c.License.ServiceProductId), cancellationToken);
            var invoiced = await NextInvoiceIssuedAsync(due.Select(c => (c.License, c.EffectiveFrom)).ToList(), cancellationToken);
            var audits = new List<AuditEntryRequest>(due.Count);
            foreach (var change in due)
            {
                skip.Add(change.Id);
                var license = change.License;
                var plan = ProductPlanDisplay.Resolve(planNames, license.ServiceProductId, change.PlanCode);
                QueueNotice(license, license.ServiceProduct, change, plan, utcNow, invoiced.Contains(license.Id));
                audits.Add(NoticeAudit(license, change, NoticeActor, null));
            }
            try
            {
                // One save for the notices, their change rows and audit rows: if another run (or a cancel) won the
                // race, nothing is sent or audited for this batch, and a notice is never sent without its audit row.
                await auditLog.WriteBatchAsync(audits, cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Another run or a cancel changed these rows first. Retry the rest of the batch now (rows the other
                // run already announced drop out of the query); the conflicting rows wait for the next run.
                var conflicted = ex.Entries.Select(e => e.Entity).OfType<LicensePriceChange>().Select(c => c.Id)
                    .ToHashSet(StringComparer.Ordinal);
                if (conflicted.Count > 0)
                    foreach (var c in due.Where(c => !conflicted.Contains(c.Id)))
                        skip.Remove(c.Id);
                db.ChangeTracker.Clear();
                continue;
            }
            queued += due.Count;
            db.ChangeTracker.Clear();
            if (due.Count < NoticeBatchSize)
                break;
        }
        return queued;
    }
}
