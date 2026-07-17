using Platform.Shared.Enums;

namespace Platform.Shared.Dtos.Billing;

public class PaymentTransactionDto
{
    public required string Id { get; set; }

    public required string InvoiceId { get; set; }

    public PaymentTransactionKind Kind { get; set; }

    public decimal Amount { get; set; }

    public PaymentMethod PaymentMethod { get; set; }

    public string? PaymentReference { get; set; }

    public string? Notes { get; set; }

    public string? ReceiptId { get; set; }

    public string? ReversesTransactionId { get; set; }

    public required string IdempotencyKey { get; set; }

    public required string PerformedBy { get; set; }

    public DateTime CreatedAt { get; set; }
}
