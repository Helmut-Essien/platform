using System.ComponentModel.DataAnnotations;
using Platform.Shared.Enums;

namespace Platform.Shared.Dtos.Billing;

public class CreateInvoiceRequest
{
    [Required(ErrorMessage = "Customer is required")]
    public required string CustomerId { get; set; }

    public string? LicenseId { get; set; }

    public string? ServiceProductId { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    public bool SendImmediately { get; set; }

    public DateTime? DueDate { get; set; }

    [Required(ErrorMessage = "Currency is required")]
    [MaxLength(3, ErrorMessage = "Currency must be 3 characters or less")]
    public required string Currency { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Subtotal must be zero or greater")]
    public decimal Subtotal { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Tax amount must be zero or greater")]
    public decimal TaxAmount { get; set; }

    [MaxLength(100, ErrorMessage = "Plan name must be 100 characters or less")]
    public string? PlanName { get; set; }

    [MaxLength(2000, ErrorMessage = "Description must be 2000 characters or less")]
    public string? Description { get; set; }

    [MaxLength(4000, ErrorMessage = "Internal notes must be 4000 characters or less")]
    public string? InternalNotes { get; set; }
}
