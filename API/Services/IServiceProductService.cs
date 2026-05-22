using Platform.Shared.Dtos.ServiceProducts;

namespace Platform.Api.Services;

public interface IServiceProductService
{
    Task<IReadOnlyList<ServiceProductDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<ServiceProductDto?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<ServiceProductDto> CreateAsync(CreateServiceProductRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    Task<ServiceProductDto> UpdateAsync(string id, UpdateServiceProductRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);
}
