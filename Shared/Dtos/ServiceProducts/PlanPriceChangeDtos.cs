using System.ComponentModel.DataAnnotations;
using Platform.Shared.Enums;

namespace Platform.Shared.Dtos.ServiceProducts;

/// <summary>The new price and when it takes effect (preview request).</summary>
public class PlanPriceChangeRequest
{
    [Range(typeof(decimal), "0.01", "9999999999999", ErrorMessage = "New price must be above zero")]
    public decimal Amount { get; set; }

    [Range(typeof(decimal), "0", "9999999999999", ErrorMessage = "Tax must be zero or greater")]
    public decimal TaxAmount { get; set; }

    /// <summary>3-letter code; blank = the server's default currency.</summary>
    [MaxLength(3, ErrorMessage = "Currency must be 3 characters")]
    public string? Currency { get; set; }

    /// <summary>New billing period in months (1-36); null keeps each license's own period.</summary>
    [Range(1, 36, ErrorMessage = "Period must be 1 to 36 months")]
    public int? PeriodMonths { get; set; }

    /// <summary>Calendar date (UTC). The first renewal invoice whose period starts on or after it uses the new price.</summary>
    public DateTime EffectiveFrom { get; set; }
}

/// <summary>Confirms a price change for the chosen licenses (normally the preview's selected rows).</summary>
public class SchedulePlanPriceChangeRequest : PlanPriceChangeRequest
{
    /// <summary>Licenses to change. Each is re-checked on the server; ones no longer eligible are skipped.</summary>
    public List<string> LicenseIds { get; set; } = [];

    /// <summary>Also make the new price the plan's default price (for new licenses).</summary>
    public bool UpdatePlanDefaultPrice { get; set; }
}

public class PlanPriceChangePreviewDto
{
    public required string PlanCode { get; set; }

    public required string PlanDisplayName { get; set; }

    public DateTime EffectiveFrom { get; set; }

    public decimal NewAmount { get; set; }

    public decimal NewTaxAmount { get; set; }

    public required string NewCurrency { get; set; }

    public int? NewPeriodMonths { get; set; }

    /// <summary>Billing:PriceChangeNoticeDays (0 = no notice emails).</summary>
    public int NoticeDays { get; set; }

    /// <summary>True when the effective date is inside the notice window: notices go out as soon as you confirm.</summary>
    public bool NoticeSentOnConfirm { get; set; }

    /// <summary>Revoked licenses on the plan (never changed, not listed).</summary>
    public int RevokedCount { get; set; }

    /// <summary>True when the plan has more licenses than one preview lists.</summary>
    public bool Truncated { get; set; }

    public List<PlanPriceChangePreviewRowDto> Rows { get; set; } = [];
}

public class PlanPriceChangePreviewRowDto
{
    public required string LicenseId { get; set; }

    public required string CustomerId { get; set; }

    public string? CustomerName { get; set; }

    public bool CustomerSuspended { get; set; }

    public LicenseStatus Status { get; set; }

    public bool IsTrial { get; set; }

    public bool IsComplimentary { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public decimal? CurrentAmount { get; set; }

    public decimal CurrentTaxAmount { get; set; }

    public string? CurrentCurrency { get; set; }

    public int CurrentPeriodMonths { get; set; }

    /// <summary>The period the license will have after the change.</summary>
    public int NewPeriodMonths { get; set; }

    /// <summary>False = can't be changed (reason in <see cref="IneligibleReason"/>).</summary>
    public bool Eligible { get; set; }

    public string? IneligibleReason { get; set; }

    /// <summary>Effective date of a change already scheduled on this license; confirming replaces it.</summary>
    public DateTime? ExistingPendingEffectiveFrom { get; set; }

    /// <summary>A renewal invoice for a period starting on/after the effective date was already issued at the old
    /// price; it is not changed, so the new price starts with the period after it.</summary>
    public bool NextInvoiceAlreadyIssued { get; set; }
}

public class SchedulePlanPriceChangeResultDto
{
    public int ScheduledCount { get; set; }

    /// <summary>Licenses whose earlier pending change was replaced.</summary>
    public int ReplacedCount { get; set; }

    /// <summary>Requested licenses skipped because they are no longer eligible (revoked, free, other plan, no price...).</summary>
    public int SkippedCount { get; set; }

    /// <summary>Notice emails queued right away (effective date inside the notice window).</summary>
    public int NoticesQueuedCount { get; set; }

    public bool PlanDefaultPriceUpdated { get; set; }
}

/// <summary>One scheduled price change on a license (pending, applied or cancelled).</summary>
public class LicensePriceChangeDto
{
    public required string Id { get; set; }

    public required string LicenseId { get; set; }

    public required string PlanCode { get; set; }

    public LicensePriceChangeStatus Status { get; set; }

    public DateTime EffectiveFrom { get; set; }

    public decimal? OldAmount { get; set; }

    public decimal OldTaxAmount { get; set; }

    public string? OldCurrency { get; set; }

    public int OldPeriodMonths { get; set; }

    public decimal NewAmount { get; set; }

    public decimal NewTaxAmount { get; set; }

    public required string NewCurrency { get; set; }

    /// <summary>Null = the license keeps its own period.</summary>
    public int? NewPeriodMonths { get; set; }

    public DateTime? NoticeQueuedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime? AppliedAt { get; set; }

    public string? AppliedInvoiceId { get; set; }

    public DateTime? CancelledAt { get; set; }

    public string? CancelledBy { get; set; }

    public string? CancelReason { get; set; }
}
