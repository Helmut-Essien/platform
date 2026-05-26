using Platform.Shared.Enums;

namespace Platform.Api.Entities;

public class License
{
    public string Id { get; set; } = NUlid.Ulid.NewUlid().ToString();

    public required string CustomerId { get; set; }

    public required string ServiceProductId { get; set; }

    public LicenseStatus Status { get; set; } = LicenseStatus.Pending;

    public DateTime? ExpiresAt { get; set; }

    public required string PlanName { get; set; }

    public string? LicenseKeyHash { get; set; }

    public string? LicenseKeyLookupHash { get; set; }

    public DateTime? LicenseKeySentAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Customer Customer { get; set; } = null!;

    public ServiceProduct ServiceProduct { get; set; } = null!;

    public ICollection<AuditLog> AuditLogs { get; set; } = [];

    public ICollection<Invoice> Invoices { get; set; } = [];
}
