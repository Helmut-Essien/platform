using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.Licenses;

public class ValidateLicenseFormRequest
{
    [Required(ErrorMessage = "Integration key is required")]
    public string IntegrationKey { get; set; } = "";

    [Required(ErrorMessage = "License key is required")]
    public string LicenseKey { get; set; } = "";

    [MaxLength(50, ErrorMessage = "Service code must be 50 characters or less")]
    public string? ServiceCode { get; set; }
}
