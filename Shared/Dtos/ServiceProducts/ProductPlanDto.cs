namespace Platform.Shared.Dtos.ServiceProducts;

/// <summary>One plan of a product. <see cref="Code"/> is what a license stores in its plan name.</summary>
public class ProductPlanDto
{
    public required string Id { get; set; }

    public required string ServiceProductId { get; set; }

    public required string Code { get; set; }

    public required string DisplayName { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; }
}
