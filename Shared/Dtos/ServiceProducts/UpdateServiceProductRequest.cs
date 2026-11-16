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

    /// <summary>
    /// Devices a new license of this product gets when none is typed (0 = unlimited). Null leaves the stored value
    /// unchanged, so a client that doesn't know the field can't wipe it; to go back to the server default send
    /// <see cref="ClearDefaultMaxDevices"/> instead.
    /// </summary>
    [Range(0, 1000, ErrorMessage = "Default max devices must be between 0 (unlimited) and 1000")]
    public int? DefaultMaxDevices { get; set; }

    /// <summary>True (with <see cref="DefaultMaxDevices"/> null) removes the product default: new licenses use the server default again.</summary>
    public bool ClearDefaultMaxDevices { get; set; }
}
