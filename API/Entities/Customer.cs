namespace Platform.Api.Entities;

public class Customer
{
    public string Id { get; set; } = NUlid.Ulid.NewUlid().ToString();

    public required string Name { get; set; }

    public required string ContactEmail { get; set; }

    public string? BillingEmail { get; set; }

    public string? TechnicalEmail { get; set; }

    public string? ContactPhone { get; set; }

    public string? InternalNotes { get; set; }

    public bool IsSuspended { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<License> Licenses { get; set; } = [];

    public ICollection<Invoice> Invoices { get; set; } = [];

    public ICollection<AuditLog> AuditLogs { get; set; } = [];
}
