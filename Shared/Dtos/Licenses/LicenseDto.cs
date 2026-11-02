using Platform.Shared.Enums;

namespace Platform.Shared.Dtos.Licenses;

public class LicenseDto
{
    public required string Id { get; set; }

    public required string CustomerId { get; set; }

    public string? CustomerName { get; set; }

    public required string ServiceProductId { get; set; }

    public string? ServiceProductCode { get; set; }

    public LicenseStatus Status { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public required string PlanName { get; set; }

    public DateTime? LicenseKeySentAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? LatestInvoiceId { get; set; }

    /// <summary>The live (non-void) first invoice that activates this license when paid, if any.</summary>
    public string? ActivationInvoiceId { get; set; }

    public string? ActivationInvoiceNumber { get; set; }

    public InvoiceStatus? ActivationInvoiceStatus { get; set; }

    public LicenseBillingDto? Billing { get; set; }

    /// <summary>On a free trial; <see cref="ExpiresAt"/> is the trial end.</summary>
    public bool IsTrial { get; set; }

    public DateTime? TrialStartedAt { get; set; }

    /// <summary>Latest date "Extend trial" can move the trial end to.</summary>
    public DateTime? TrialMaxEndsAt { get; set; }

    /// <summary>Free until changed: never invoiced or auto-suspended.</summary>
    public bool IsComplimentary { get; set; }

    /// <summary>Devices allowed at once; null = unlimited.</summary>
    public int? MaxDevices { get; set; }
}
