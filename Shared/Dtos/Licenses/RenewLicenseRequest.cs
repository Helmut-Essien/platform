using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.Licenses;

public class RenewLicenseRequest
{
    [Required(ErrorMessage = "Expiry date is required")]
    public DateTime? ExpiresAt { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Subtotal must be zero or greater")]
    public decimal Subtotal { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "Tax amount must be zero or greater")]
    public decimal TaxAmount { get; set; }

    [Required(ErrorMessage = "Currency is required")]
    [MaxLength(3, ErrorMessage = "Currency must be 3 characters or less")]
    public string Currency { get; set; } = "USD";

    public DateTime? DueDate { get; set; }

    [MaxLength(2000, ErrorMessage = "Description must be 2000 characters or less")]
    public string? Description { get; set; }
}
