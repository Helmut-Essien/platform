using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.ServiceProducts;

public class CreateServiceProductRequest
{
    [Required]
    [MaxLength(200)]
    public required string Name { get; set; }

    [Required]
    [MaxLength(50)]
    public required string Code { get; set; }

    [MaxLength(2000)]
    public string? Description { get; set; }

    public bool IsAvailableForSale { get; set; } = true;
}
