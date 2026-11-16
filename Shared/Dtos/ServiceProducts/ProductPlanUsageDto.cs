namespace Platform.Shared.Dtos.ServiceProducts;

/// <summary>A distinct <c>PlanName</c> value found on a product's licenses, with how many licenses carry it.</summary>
public class ProductPlanUsageDto
{
    public required string PlanName { get; set; }

    /// <summary>All licenses with exactly this name, revoked ones included.</summary>
    public int LicenseCount { get; set; }

    /// <summary>Licenses a remap would change (everything except revoked ones).</summary>
    public int RemappableCount { get; set; }

    /// <summary>True when the name is exactly the code of an active plan of this product (nothing to map).</summary>
    public bool IsActivePlanCode { get; set; }
}
