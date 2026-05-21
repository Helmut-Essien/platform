using Platform.Shared.Enums;

namespace Platform.Shared.Dtos.Billing;

public class ReceiptDto
{
    public required string Id { get; set; }

    public required string InvoiceId { get; set; }

    public required string ReceiptNumber { get; set; }

    public decimal AmountPaid { get; set; }

    public DateTime PaidAt { get; set; }

    public PaymentMethod PaymentMethod { get; set; }

    public string? PaymentReference { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
}
