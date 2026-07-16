using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.Settings;

public class InvoicePaymentOptionDto
{
    [MaxLength(100, ErrorMessage = "Payment method must be 100 characters or less")]
    public string Method { get; set; } = "";

    [MaxLength(1000, ErrorMessage = "Payment details must be 1000 characters or less")]
    public string? Details { get; set; }
}
