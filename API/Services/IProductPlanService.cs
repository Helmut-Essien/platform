using Platform.Shared.Dtos.ServiceProducts;

namespace Platform.Api.Services;

public interface IProductPlanService
{
    Task<IReadOnlyList<ProductPlanDto>> ListAsync(string serviceProductId, CancellationToken cancellationToken = default);

    Task<ProductPlanDto> CreateAsync(string serviceProductId, CreateProductPlanRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    Task<ProductPlanDto> UpdateAsync(string serviceProductId, string planId, UpdateProductPlanRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes a plan only while no license of the product carries its code; otherwise deactivate it instead.</summary>
    Task DeleteAsync(string serviceProductId, string planId, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    /// <summary>Every distinct plan name on the product's licenses with counts: what still needs mapping.</summary>
    Task<IReadOnlyList<ProductPlanUsageDto>> GetUsageAsync(string serviceProductId, CancellationToken cancellationToken = default);

    /// <summary>One-off mapping of an old free-text plan name onto a plan code (audited; supports a dry run).</summary>
    Task<RemapProductPlanResultDto> RemapAsync(string serviceProductId, RemapProductPlanRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);
}
