using System.ComponentModel.DataAnnotations;
using Platform.Shared.Enums;

namespace Platform.Shared.Dtos.Billing;

public class RecordReceiptRequest
{
    [Range(typeof(decimal), "0.01", "79228162514264337593543950335", ErrorMessage = "Amount must be greater than zero")]
    public decimal AmountPaid { get; set; }

    [Required(ErrorMessage = "Payment method is required")]
    public PaymentMethod PaymentMethod { get; set; }

    [MaxLength(200, ErrorMessage = "Reference must be 200 characters or less")]
    public string? PaymentReference { get; set; }

    [MaxLength(2000, ErrorMessage = "Notes must be 2000 characters or less")]
    public string? Notes { get; set; }

    public DateTime? PaidAt { get; set; }
}
