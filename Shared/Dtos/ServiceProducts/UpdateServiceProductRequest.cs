using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.ServiceProducts;

public class UpdateServiceProductRequest
{
    [Required(ErrorMessage = "Name is required")]
    [MaxLength(200, ErrorMessage = "Name must be 200 characters or less")]
    public required string Name { get; set; }

    [MaxLength(2000, ErrorMessage = "Description must be 2000 characters or less")]
    public string? Description { get; set; }

    public bool IsAvailableForSale { get; set; }

    /// <summary>Devices a new license of this product gets when none is typed. Null = server default; 0 = unlimited.</summary>
    [Range(0, 1000, ErrorMessage = "Default max devices must be between 0 (unlimited) and 1000")]
    public int? DefaultMaxDevices { get; set; }
}
