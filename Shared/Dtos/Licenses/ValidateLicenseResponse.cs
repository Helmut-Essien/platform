namespace Platform.Shared.Dtos.Licenses;

public class ValidateLicenseResponse
{
    public bool IsValid { get; set; }

    public string? PlanName { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public string? Message { get; set; }
}
