using Platform.Shared.Enums;

namespace Platform.Shared.Dtos.Email;

public class EmailDeliveryDto
{
    public required string Id { get; set; }
    public EmailDeliveryKind Kind { get; set; }
    public EmailDeliveryStatus Status { get; set; }
    public required string ToEmail { get; set; }
    public required string Subject { get; set; }
    public string? CustomerId { get; set; }
    public string? LicenseId { get; set; }
    public string? InvoiceId { get; set; }
    public string? ReceiptId { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
}
