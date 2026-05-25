namespace Platform.Shared.Dtos.Licenses;

public class ValidateLicenseRequest
{
    public required string LicenseKey { get; set; }

    public string? ServiceCode { get; set; }
}
