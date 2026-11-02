using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.Licenses;

public class EndComplimentaryRequest
{
    /// <summary>The price to bill from now on (amount above zero required).</summary>
    [Required]
    public required LicenseBillingDto Billing { get; set; }

    /// <summary>
    /// When the free period ends and the first paid period starts (the first invoice is due then). Defaults to
    /// the current expiry, or 14 days from today when the license has none.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Create and email the first invoice now instead of waiting for the renewal scan.</summary>
    public bool CreateInvoiceNow { get; set; } = true;
}
