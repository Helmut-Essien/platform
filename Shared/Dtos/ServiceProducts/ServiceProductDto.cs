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

    /// <summary>Device limit for new licenses of this product when none is typed (null = server default, 0 = unlimited).</summary>
    public int? DefaultMaxDevices { get; set; }

    /// <summary>The product's plan catalog (active and inactive), in dropdown order. Empty = free-text plan names.</summary>
    public List<ProductPlanDto> Plans { get; set; } = [];
}
