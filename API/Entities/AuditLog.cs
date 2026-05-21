using Platform.Shared.Enums;

namespace Platform.Api.Entities;

public class AuditLog
{
    public string Id { get; set; } = NUlid.Ulid.NewUlid().ToString();

    public string? CustomerId { get; set; }

    public string? LicenseId { get; set; }

    public string? InvoiceId { get; set; }

    public AuditAction Action { get; set; }

    public required string PerformedBy { get; set; }

    public string? DetailsJson { get; set; }

    public string? IpAddress { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public Customer? Customer { get; set; }

    public License? License { get; set; }

    public Invoice? Invoice { get; set; }
}
