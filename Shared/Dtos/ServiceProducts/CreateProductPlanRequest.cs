using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.ServiceProducts;

public class CreateProductPlanRequest
{
    /// <summary>Trimmed and lowercased on save. Letters, digits, '-' and '_' only; cannot be changed later.</summary>
    [Required(ErrorMessage = "Code is required")]
    [MaxLength(50, ErrorMessage = "Code must be 50 characters or less")]
    public required string Code { get; set; }

    [Required(ErrorMessage = "Display name is required")]
    [MaxLength(100, ErrorMessage = "Display name must be 100 characters or less")]
    public required string DisplayName { get; set; }

    [Range(0, 100000, ErrorMessage = "Sort order must be between 0 and 100000")]
    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;
}
