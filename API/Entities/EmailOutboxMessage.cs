using Platform.Shared.Enums;

namespace Platform.Api.Entities;

public class EmailOutboxMessage
{
    public string Id { get; set; } = NUlid.Ulid.NewUlid().ToString();
    public EmailDeliveryKind Kind { get; set; }
    public EmailDeliveryStatus Status { get; set; } = EmailDeliveryStatus.Pending;
    public required string ToEmail { get; set; }
    public required string Subject { get; set; }
    public required string HtmlBody { get; set; }
    public string? CustomerId { get; set; }
    public string? LicenseId { get; set; }
    public string? InvoiceId { get; set; }
    public string? ReceiptId { get; set; }
    public byte[]? EncryptedPayload { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public string? ProviderMessageId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }

    public Customer? Customer { get; set; }
    public License? License { get; set; }
    public Invoice? Invoice { get; set; }
    public Receipt? Receipt { get; set; }
}
