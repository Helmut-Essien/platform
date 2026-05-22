using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.Licenses;

public class UpdateLicenseRequest
{
    [Required]
    [MaxLength(100)]
    public required string PlanName { get; set; }

    public DateTime? ExpiresAt { get; set; }
}
