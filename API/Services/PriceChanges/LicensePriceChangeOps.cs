using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Helpers;
using Platform.Api.Services.Email;
using Platform.Shared.Dtos.ServiceProducts;
using Platform.Shared.Enums;

namespace Platform.Api.Services.PriceChanges;

/// <summary>
/// Shared helpers for scheduled license price changes. Lock order everywhere: plan rows (when taken), then license
/// rows, then price-change rows. Callers that cancel must already hold the license row lock (or be outside
/// PostgreSQL), so a concurrent schedule or renewal can't interleave.
/// </summary>
internal static class LicensePriceChangeOps
{
    internal const string ReasonManualPrice = "superseded by a manual price edit";
    internal const string ReasonPlanChanged = "license moved to another plan";
    internal const string ReasonReplaced = "replaced by a newer price change";
    internal const string ReasonComplimentary = "license made complimentary";
    internal const string ReasonRevoked = "license revoked";
    internal const string ReasonCancelled = "cancelled by an admin";

    internal static LicensePriceChangeDto Map(LicensePriceChange c) => new()
    {
        Id = c.Id,
        LicenseId = c.LicenseId,
        PlanCode = c.PlanCode,
        Status = c.Status,
        EffectiveFrom = c.EffectiveFrom,
        OldAmount = c.OldAmount,
        OldTaxAmount = c.OldTaxAmount,
        OldCurrency = c.OldCurrency,
        OldPeriodMonths = c.OldPeriodMonths,
        NewAmount = c.NewAmount,
        NewTaxAmount = c.NewTaxAmount,
        NewCurrency = c.NewCurrency,
        NewPeriodMonths = c.NewPeriodMonths,
        NoticeQueuedAt = c.NoticeQueuedAt,
        CreatedAt = c.CreatedAt,
        CreatedBy = c.CreatedBy,
        AppliedAt = c.AppliedAt,
        AppliedInvoiceId = c.AppliedInvoiceId,
        CancelledAt = c.CancelledAt,
        CancelledBy = c.CancelledBy,
        CancelReason = c.CancelReason
    };

    /// <summary>The pending change of each license, in one query (partial unique index: at most one each).</summary>
    internal static async Task<Dictionary<string, LicensePriceChangeDto>> LoadPendingAsync(
        AppDbContext db, IReadOnlyCollection<string> licenseIds, CancellationToken cancellationToken)
    {
        if (licenseIds.Count == 0)
            return new Dictionary<string, LicensePriceChangeDto>(StringComparer.Ordinal);
        var rows = await db.LicensePriceChanges.IgnoreQueryFilters().AsNoTracking()
            .Where(c => licenseIds.Contains(c.LicenseId) && c.Status == LicensePriceChangeStatus.Pending)
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(c => c.LicenseId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Map(g.OrderByDescending(c => c.CreatedAt).First()), StringComparer.Ordinal);
    }

    /// <summary>The price fields a manual edit is compared on (auto-renew on/off is not a price change).</summary>
    internal static (decimal? Amount, decimal Tax, string? Currency, int Period) PriceOf(License license) =>
        (license.BillingAmount, license.BillingTaxAmount, license.BillingCurrency, license.BillingPeriodMonths);

    /// <summary>
    /// Cancels the pending change of each given (tracked) license. Writes one audit row per cancelled change and,
    /// when <paramref name="notifyCustomer"/> is set and the customer was already sent the notice, queues a
    /// "price change cancelled" email showing the license's current price. Adds the changes to the context; the
    /// caller's next save (or the audit write) persists them in its transaction. Returns how many were cancelled.
    /// </summary>
    internal static async Task<int> CancelPendingAsync(
        AppDbContext db,
        IAuditLogService auditLog,
        IEmailOutboxService? outbox,
        EmailTemplateService? templates,
        IReadOnlyCollection<License> licenses,
        string reason,
        string performedBy,
        string? ipAddress,
        bool notifyCustomer,
        CancellationToken cancellationToken,
        Func<LicensePriceChange, bool>? only = null)
    {
        if (licenses.Count == 0)
            return 0;
        var ids = licenses.Select(l => l.Id).Distinct(StringComparer.Ordinal).ToList();
        var pending = await db.LicensePriceChanges.IgnoreQueryFilters()
            .Where(c => ids.Contains(c.LicenseId) && c.Status == LicensePriceChangeStatus.Pending)
            .ToListAsync(cancellationToken);
        if (only is not null)
            pending = pending.Where(only).ToList();
        if (pending.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        var byId = licenses.GroupBy(l => l.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var toNotify = notifyCustomer && outbox is not null && templates is not null
            ? pending.Where(c => c.NoticeQueuedAt is not null).ToList()
            : [];
        Dictionary<string, Customer> customers = new(StringComparer.Ordinal);
        Dictionary<string, ServiceProduct> products = new(StringComparer.Ordinal);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> planNames =
            new Dictionary<string, IReadOnlyDictionary<string, string>>();
        if (toNotify.Count > 0)
        {
            var notifyLicenses = toNotify.Select(c => byId[c.LicenseId]).ToList();
            var customerIds = notifyLicenses.Select(l => l.CustomerId).Distinct().ToList();
            var productIds = notifyLicenses.Select(l => l.ServiceProductId).Distinct().ToList();
            customers = await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, StringComparer.Ordinal, cancellationToken);
            products = await db.ServiceProducts.AsNoTracking().Where(p => productIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, StringComparer.Ordinal, cancellationToken);
            planNames = await ProductPlanDisplay.LoadAsync(db, productIds, cancellationToken);
        }

        var audits = new List<AuditEntryRequest>(pending.Count);
        foreach (var change in pending)
        {
            var license = byId[change.LicenseId];
            change.Status = LicensePriceChangeStatus.Cancelled;
            change.CancelledAt = now;
            change.CancelledBy = performedBy;
            change.CancelReason = reason;
            var notified = false;
            if (toNotify.Contains(change)
                && customers.TryGetValue(license.CustomerId, out var customer)
                && products.TryGetValue(license.ServiceProductId, out var product))
            {
                var plan = ProductPlanDisplay.Resolve(planNames, license.ServiceProductId, change.PlanCode);
                var mail = templates!.PriceChangeCancelled(customer, product, license, change, plan);
                outbox!.Enqueue(EmailDeliveryKind.PriceChangeCancelled, CustomerContactResolver.Billing(customer),
                    mail.Subject, mail.Html, license.CustomerId, license.Id);
                notified = true;
            }
            audits.Add(new AuditEntryRequest(AuditAction.LicensePriceChangeCancelled, performedBy, license.CustomerId,
                license.Id, null, AuditJson.Serialize(new
                {
                    priceChangeId = change.Id,
                    reason,
                    effectiveFrom = change.EffectiveFrom,
                    newAmount = change.NewAmount,
                    currency = change.NewCurrency,
                    customerNotified = notified
                }), ipAddress));
        }
        await auditLog.WriteBatchAsync(audits, cancellationToken);
        return pending.Count;
    }

    /// <summary>
    /// Row-locks the given licenses (id order, so two callers can't deadlock). No-op outside a transaction or on a
    /// non-relational provider.
    /// </summary>
    internal static async Task LockLicenseRowsAsync(AppDbContext db, IReadOnlyCollection<string> licenseIds, CancellationToken cancellationToken)
    {
        if (licenseIds.Count == 0 || !db.Database.IsRelational() || db.Database.CurrentTransaction is null)
            return;
        var ids = licenseIds.Distinct(StringComparer.Ordinal).OrderBy(i => i, StringComparer.Ordinal).ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "Licenses" WHERE "Id" = ANY({ids}) ORDER BY "Id" FOR UPDATE""", cancellationToken);
    }
}
