using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.Licenses;

public class UpdateLicenseRequest
{
    [Required(ErrorMessage = "Plan name is required")]
    [MaxLength(100, ErrorMessage = "Plan name must be 100 characters or less")]
    public required string PlanName { get; set; }

    public DateTime? ExpiresAt { get; set; }

    /// <summary>Recurring price for automatic renewal invoices; null leaves the license's current setting unchanged.</summary>
    public LicenseBillingDto? Billing { get; set; }
}
