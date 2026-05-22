namespace Platform.Shared.Dtos.ServiceProducts;

public class ServiceProductDto
{
    public required string Id { get; set; }

    public required string Name { get; set; }

    public required string Code { get; set; }

    public string? Description { get; set; }

    public bool IsAvailableForSale { get; set; }

    public bool HasActiveIntegrationKey { get; set; }

    public int LicenseCount { get; set; }
}
