using Platform.Shared.Enums;

namespace Platform.Shared.Dtos.Licenses;

public class LicenseDto
{
    public required string Id { get; set; }

    public required string CustomerId { get; set; }

    public string? CustomerName { get; set; }

    public required string ServiceProductId { get; set; }

    public string? ServiceProductCode { get; set; }

    public LicenseStatus Status { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public required string PlanName { get; set; }

    public DateTime? LicenseKeySentAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? LatestInvoiceId { get; set; }
}
