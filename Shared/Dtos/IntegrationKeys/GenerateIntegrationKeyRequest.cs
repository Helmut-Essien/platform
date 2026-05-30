using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.IntegrationKeys;

public class GenerateIntegrationKeyRequest
{
    [Required(ErrorMessage = "Service product is required")]
    public string ServiceProductId { get; set; } = "";
}
