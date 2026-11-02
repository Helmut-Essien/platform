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
    /// Term set by a paid activation invoice: kept when the license already has a future expiry; otherwise one
    /// period from the payment day (calendar-date convention, like renewals). A license with no expiry and no
    /// known period (one-off price) stays perpetual.
    /// </summary>
    internal static DateTime? ExpiryAfterPayment(DateTime? currentExpiry, int? periodMonths, DateTime paidAt, DateTime now)
    {
        if (currentExpiry is not null && !Helpers.DateTimeNormalizer.IsPastExpiry(currentExpiry, now))
            return currentExpiry;
        if (currentExpiry is null && periodMonths is null)
            return null;

        var start = (paidAt > now ? now : paidAt).Date;
        var months = Math.Clamp(periodMonths ?? 12, 1, 36);
        return DateTime.SpecifyKind(Renewals.PeriodMath.AddMonths(DateTime.SpecifyKind(start, DateTimeKind.Utc), months, start.Day), DateTimeKind.Utc);
    }
}
