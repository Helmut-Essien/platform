using Platform.Shared.Enums;

namespace Platform.Api.Entities;

public class Invoice
{
    public string Id { get; set; } = NUlid.Ulid.NewUlid().ToString();

    public required string CustomerId { get; set; }

    public string? LicenseId { get; set; }

    public string? ServiceProductId { get; set; }

    public required string InvoiceNumber { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    public DateTime IssueDate { get; set; } = DateTime.UtcNow;

    public DateTime? DueDate { get; set; }

    public required string Currency { get; set; }

    public decimal Subtotal { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public string? PlanName { get; set; }

    public string? Description { get; set; }

    public string? InternalNotes { get; set; }

    /// <summary>
    /// Set on automatic/explicit renewal invoices: the license expiry this invoice renews (the period start).
    /// Unique per license among non-void invoices, so each period is invoiced at most once.
    /// </summary>
    public DateTime? RenewalForExpiresAt { get; set; }

    /// <summary>Period length captured when the renewal invoice was created (later price edits don't change it).</summary>
    public int? RenewalPeriodMonths { get; set; }

    /// <summary>When the paid renewal was applied to the license (null = not applied / reverted).</summary>
    public DateTime? RenewalAppliedAt { get; set; }

    public DateTime? RenewalPreviousExpiresAt { get; set; }

    /// <summary>Expiry the renewal set; a reversal only reverts if the license still has exactly this value.</summary>
    public DateTime? RenewalNewExpiresAt { get; set; }

    /// <summary>Random per-invoice secret mixed into the signed public pay link; cleared to revoke the link.</summary>
    public string? PayLinkNonce { get; set; }

    public DateTime? PayLinkExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Customer Customer { get; set; } = null!;

    public License? License { get; set; }

    public ServiceProduct? ServiceProduct { get; set; }

    public ICollection<Receipt> Receipts { get; set; } = [];

    public ICollection<PaymentTransaction> PaymentTransactions { get; set; } = [];

    public ICollection<AuditLog> AuditLogs { get; set; } = [];
}
