using Platform.Shared.Enums;

namespace Platform.Shared.Dtos.Billing;

public class InvoiceDto
{
    public required string Id { get; set; }

    public required string CustomerId { get; set; }

    public string? CustomerName { get; set; }

    public string? LicenseId { get; set; }

    public string? ServiceProductId { get; set; }

    public string? ServiceProductCode { get; set; }

    public required string InvoiceNumber { get; set; }

    public InvoiceStatus Status { get; set; }

    public DateTime IssueDate { get; set; }

    public DateTime? DueDate { get; set; }

    public required string Currency { get; set; }

    public decimal Subtotal { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal AmountPaid { get; set; }

    public decimal AmountDue { get; set; }

    public string? PlanName { get; set; }

    public string? Description { get; set; }

    public string? InternalNotes { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<ReceiptDto> Receipts { get; set; } = [];

    public List<PaymentTransactionDto> Transactions { get; set; } = [];
}
