using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.Licenses;

public class ValidateLicenseRequest
{
    [Required]
    public required string LicenseKey { get; set; }

    public string? ServiceCode { get; set; }
}
