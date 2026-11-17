using Platform.Shared.Enums;

namespace Platform.Api.Entities;

/// <summary>
/// A price change scheduled for one license (usually from a plan-wide price change). While
/// <see cref="Status"/> is Pending, the first renewal invoice whose period starts on or after
/// <see cref="EffectiveFrom"/> uses the new price and copies it onto the license (the license stays the source of
/// truth; invoices already issued never change). At most one pending change per license (partial unique index).
/// </summary>
public class LicensePriceChange
{
    public string Id { get; set; } = NUlid.Ulid.NewUlid().ToString();

    public required string LicenseId { get; set; }

    public License License { get; set; } = null!;

    /// <summary>Groups the licenses of one plan-wide change (also in the audit log).</summary>
    public required string BatchId { get; set; }

    /// <summary>The plan code the license was on when scheduled; a license moved to another plan doesn't take it.</summary>
    public required string PlanCode { get; set; }

    public LicensePriceChangeStatus Status { get; set; } = LicensePriceChangeStatus.Pending;

    /// <summary>Calendar date (00:00 UTC).</summary>
    public DateTime EffectiveFrom { get; set; }

    public decimal? OldAmount { get; set; }

    public decimal OldTaxAmount { get; set; }

    public string? OldCurrency { get; set; }

    public int OldPeriodMonths { get; set; }

    public decimal NewAmount { get; set; }

    public decimal NewTaxAmount { get; set; }

    public required string NewCurrency { get; set; }

    /// <summary>Null = the license keeps its own billing period.</summary>
    public int? NewPeriodMonths { get; set; }

    /// <summary>When the customer notice was queued (sent once per change).</summary>
    public DateTime? NoticeQueuedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? CreatedBy { get; set; }

    public DateTime? AppliedAt { get; set; }

    public string? AppliedInvoiceId { get; set; }

    public DateTime? CancelledAt { get; set; }

    public string? CancelledBy { get; set; }

    public string? CancelReason { get; set; }

    /// <summary>Optimistic concurrency token, incremented on every update (notice worker vs cancel vs renewal).</summary>
    public long Version { get; set; }
}
