using Platform.Shared.Enums;

namespace Platform.Api.Entities;

public class License
{
    public string Id { get; set; } = NUlid.Ulid.NewUlid().ToString();

    public required string CustomerId { get; set; }

    public required string ServiceProductId { get; set; }

    public LicenseStatus Status { get; set; } = LicenseStatus.Pending;

    public DateTime? ExpiresAt { get; set; }

    public required string PlanName { get; set; }

    public string? LicenseKeyHash { get; set; }

    public string? LicenseKeyLookupHash { get; set; }

    public DateTime? LicenseKeySentAt { get; set; }

    /// <summary>
    /// When set, this license was auto-suspended because the linked invoice became overdue.
    /// Cleared on manual suspend/activate/reactivate so only overdue-driven suspensions auto-restore.
    /// </summary>
    public string? AutoSuspendedForOverdueInvoiceId { get; set; }

    /// <summary>Recurring price per period (subtotal, before tax). Null or 0 = no automatic renewal invoices.</summary>
    public decimal? BillingAmount { get; set; }

    public decimal BillingTaxAmount { get; set; }

    /// <summary>ISO currency of <see cref="BillingAmount"/>; required when a price is set.</summary>
    public string? BillingCurrency { get; set; }

    /// <summary>Length of one billing period in months (1–36).</summary>
    public int BillingPeriodMonths { get; set; } = 12;

    /// <summary>
    /// Day of month renewals land on (1–31), so month-end anniversaries survive short months
    /// (Jan 31 → Feb 28 → Mar 31). Set whenever an admin sets the expiry.
    /// </summary>
    public int? BillingAnchorDay { get; set; }

    /// <summary>Per-license opt-out for automatic renewal invoices (only licenses with a price are invoiced).</summary>
    public bool AutoRenewInvoice { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optimistic concurrency token. Incremented on every update.
    /// </summary>
    public long Version { get; set; }

    public Customer Customer { get; set; } = null!;

    public ServiceProduct ServiceProduct { get; set; } = null!;

    public ICollection<AuditLog> AuditLogs { get; set; } = [];

    public ICollection<Invoice> Invoices { get; set; } = [];
}
