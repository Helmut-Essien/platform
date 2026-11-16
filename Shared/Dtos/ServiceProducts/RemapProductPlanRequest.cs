using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.ServiceProducts;

/// <summary>Moves every non-revoked license of a product from an old free-text plan name onto a plan code.</summary>
public class RemapProductPlanRequest
{
    /// <summary>The exact old value, as listed by the usage endpoint (case-sensitive, not trimmed).</summary>
    [Required(ErrorMessage = "The old plan name is required")]
    [MaxLength(100)]
    public required string FromPlanName { get; set; }

    /// <summary>Code of an active plan of the same product.</summary>
    [Required(ErrorMessage = "The target plan code is required")]
    [MaxLength(50)]
    public required string ToCode { get; set; }

    /// <summary>When true nothing is changed; the result only reports how many licenses would be.</summary>
    public bool DryRun { get; set; }
}
