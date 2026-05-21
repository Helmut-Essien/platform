using Platform.Shared.Enums;

namespace Platform.Api.Entities;

public class Invoice
{
    public string Id { get; set; } = NUlid.Ulid.NewUlid().ToString();

    public required string CustomerId { get; set; }

    public string? LicenseId { get; set; }

    public string? ServiceProductId { get; set; }

    public required string InvoiceNumber { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    public DateTime IssueDate { get; set; } = DateTime.UtcNow;

    public DateTime? DueDate { get; set; }

    public required string Currency { get; set; }

    public decimal Subtotal { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public string? PlanName { get; set; }

    public string? Description { get; set; }

    public string? InternalNotes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Customer Customer { get; set; } = null!;

    public License? License { get; set; }

    public ServiceProduct? ServiceProduct { get; set; }

    public ICollection<Receipt> Receipts { get; set; } = [];

    public ICollection<AuditLog> AuditLogs { get; set; } = [];
}
