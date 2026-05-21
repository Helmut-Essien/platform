using Platform.Shared.Enums;

namespace Platform.Api.Entities;

public class Receipt
{
    public string Id { get; set; } = NUlid.Ulid.NewUlid().ToString();

    public required string InvoiceId { get; set; }

    public required string ReceiptNumber { get; set; }

    public decimal AmountPaid { get; set; }

    public DateTime PaidAt { get; set; } = DateTime.UtcNow;

    public PaymentMethod PaymentMethod { get; set; }

    public string? PaymentReference { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Invoice Invoice { get; set; } = null!;
}
