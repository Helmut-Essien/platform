using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.Billing;

public class ReverseReceiptRequest
{
    [Required(ErrorMessage = "Idempotency key is required")]
    [MaxLength(100, ErrorMessage = "Idempotency key must be 100 characters or less")]
    public required string IdempotencyKey { get; set; }

    [Required(ErrorMessage = "Reason is required")]
    [MaxLength(2000, ErrorMessage = "Reason must be 2000 characters or less")]
    public required string Reason { get; set; }
}
