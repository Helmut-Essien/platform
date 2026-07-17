using Platform.Shared.Enums;

namespace Platform.Api.Entities;

public class PaymentTransaction
{
    public string Id { get; set; } = NUlid.Ulid.NewUlid().ToString();

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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Invoice Invoice { get; set; } = null!;

    public Receipt? Receipt { get; set; }

    public PaymentTransaction? ReversesTransaction { get; set; }
}
