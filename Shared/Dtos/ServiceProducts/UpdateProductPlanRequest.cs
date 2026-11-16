using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.ServiceProducts;

/// <summary>Edits a plan. The code is stable and is not part of this request.</summary>
public class UpdateProductPlanRequest
{
    [Required(ErrorMessage = "Display name is required")]
    [MaxLength(100, ErrorMessage = "Display name must be 100 characters or less")]
    public required string DisplayName { get; set; }

    [Range(0, 100000, ErrorMessage = "Sort order must be between 0 and 100000")]
    public int SortOrder { get; set; }

    /// <summary>False deactivates: blocks new issues and edits onto the plan; existing keys keep working.</summary>
    public bool IsActive { get; set; } = true;
}
