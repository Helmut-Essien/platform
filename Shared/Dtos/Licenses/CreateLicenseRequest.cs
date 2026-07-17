using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.Licenses;

public class CreateLicenseRequest
{
    [Required(ErrorMessage = "Customer is required")]
    public required string CustomerId { get; set; }

    [Required(ErrorMessage = "Service is required")]
    public required string ServiceProductId { get; set; }

    [Required(ErrorMessage = "Plan name is required")]
    [MaxLength(100, ErrorMessage = "Plan name must be 100 characters or less")]
    public required string PlanName { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public bool CreateInvoice { get; set; }

    public bool SendInvoice { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal InvoiceSubtotal { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal InvoiceTaxAmount { get; set; }

    [MaxLength(3)]
    public string InvoiceCurrency { get; set; } = "USD";

    public DateTime? InvoiceDueDate { get; set; }
}
