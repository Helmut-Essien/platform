namespace Platform.Api.Entities;

/// <summary>
/// One plan a product sells (for example NEEDLEBASE: starter, workshop, production). A license's
/// <see cref="License.PlanName"/> stores the plan <see cref="Code"/>, so the validate response and the token's
/// <c>plan</c> claim are unchanged. Limits and entitlements stay in each product, not here.
/// </summary>
public class ProductPlan
{
    public string Id { get; set; } = NUlid.Ulid.NewUlid().ToString();

    public required string ServiceProductId { get; set; }

    public ServiceProduct ServiceProduct { get; set; } = null!;

    /// <summary>Stable, lowercase, unique per product (letters, digits, '-' and '_'). Never edited after creation.</summary>
    public required string Code { get; set; }

    /// <summary>What the admin sees; may change.</summary>
    public required string DisplayName { get; set; }

    /// <summary>Dropdown order (ascending, then code).</summary>
    public int SortOrder { get; set; }

    /// <summary>Inactive plans cannot be chosen when issuing or editing a license; keys already on one keep working.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
