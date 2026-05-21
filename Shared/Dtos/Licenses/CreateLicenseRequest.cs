namespace Platform.Shared.Dtos.Licenses;

public class CreateLicenseRequest
{
    public required string CustomerId { get; set; }

    public required string ServiceProductId { get; set; }

    public required string PlanName { get; set; }

    public DateTime? ExpiresAt { get; set; }
}
