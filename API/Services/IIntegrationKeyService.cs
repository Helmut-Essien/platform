using Platform.Shared.Dtos.IntegrationKeys;

namespace Platform.Api.Services;

public interface IIntegrationKeyService
{
    Task<IReadOnlyList<IntegrationKeyDto>> ListAsync(string? serviceProductId = null, CancellationToken cancellationToken = default);

    Task<CreateIntegrationKeyResponse> CreateAsync(
        string serviceProductId,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default);

    Task<IntegrationKeyDto> RevokeAsync(
        string id,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default);
}
