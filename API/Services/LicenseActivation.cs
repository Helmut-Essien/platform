using Platform.Api.Entities;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

/// <summary>
/// The one place a license is switched to Active, shared by manual activation (<see cref="LicenseService"/>)
/// and automatic activation when its first invoice is paid (<see cref="BillingService"/>).
/// Callers own the transaction and the row lock; they clear the deny list after committing.
/// </summary>
internal static class LicenseActivation
{
    /// <summary>
    /// Marks the tracked license Active and, when <paramref name="deliverKey"/> is set, generates a new key and
    /// queues it to the technical contact through the outbox (saved with the caller's SaveChanges, so it survives
    /// crashes and app-pool recycles). A license that has never had a key must get one.
    /// </summary>
    internal static async Task ActivateAsync(
        License license,
        ILicenseKeyDeliveryService keyDelivery,
        bool deliverKey,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (!deliverKey && string.IsNullOrEmpty(license.LicenseKeyHash))
            throw new InvalidOperationException("A new license must be delivered by email when first activated.");

        license.Status = LicenseStatus.Active;
        license.AutoSuspendedForOverdueInvoiceId = null;
        license.UpdatedAt = now;

        if (deliverKey)
            await keyDelivery.DeliverNewKeyAsync(license, isRenewal: false, cancellationToken);
    }

    /// <summary>
    /// Term set by a paid activation invoice. The payment always buys at least one full period from the payment day
    /// (calendar-date convention, like renewals), so a customer who pays late never gets less than they paid for:
    /// the new expiry is the later of the current expiry and payment day + period. The billing anchor day is the
    /// payment day (not the month-end-clamped expiry), so 31 Jan renews on 28 Feb, then 31 Mar.
    /// A one-off price (no period) leaves a future expiry alone and makes a lapsed or open-ended license perpetual
    /// (a one-off payment buys no fixed term, and it must not activate a license that is already expired).
    /// <c>AnchorDay</c> null = leave the anchor unchanged.
    /// </summary>
    internal static (DateTime? Expiry, int? AnchorDay) ExpiryAfterPayment(DateTime? currentExpiry, int? periodMonths, DateTime paidAt, DateTime now)
    {
        var currentIsFuture = currentExpiry is not null && !Helpers.DateTimeNormalizer.IsPastExpiry(currentExpiry, now);
        if (periodMonths is null)
            return currentIsFuture ? (currentExpiry, null) : (null, null);

        var start = DateTime.SpecifyKind((paidAt > now ? now : paidAt).Date, DateTimeKind.Utc);
        var months = Math.Clamp(periodMonths.Value, 1, 36);
        var paidThrough = Renewals.PeriodMath.AddMonths(start, months, start.Day);
        if (currentIsFuture && currentExpiry!.Value >= paidThrough)
            return (currentExpiry, null);
        return (paidThrough, start.Day);
    }
}
