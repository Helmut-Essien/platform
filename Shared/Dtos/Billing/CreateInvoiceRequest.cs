using Platform.Shared.Enums;

namespace Platform.Shared.Dtos.Billing;

public class CreateInvoiceRequest
{
    public required string CustomerId { get; set; }

    public string? LicenseId { get; set; }

    public string? ServiceProductId { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Sent;

    public DateTime? DueDate { get; set; }

    public required string Currency { get; set; }

    public decimal Subtotal { get; set; }

    public decimal TaxAmount { get; set; }

    public string? PlanName { get; set; }

    public string? Description { get; set; }

    public string? InternalNotes { get; set; }
}
