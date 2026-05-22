using Platform.Shared.Enums;

namespace Platform.Shared.Dtos.Audit;

public class AuditLogDto
{
    public required string Id { get; set; }

    public string? CustomerId { get; set; }

    public string? LicenseId { get; set; }

    public string? InvoiceId { get; set; }

    public AuditAction Action { get; set; }

    public required string PerformedBy { get; set; }

    public string? DetailsJson { get; set; }

    public string? IpAddress { get; set; }

    public DateTime Timestamp { get; set; }
}
