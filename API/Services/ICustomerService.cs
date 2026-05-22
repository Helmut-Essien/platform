using Platform.Shared.Dtos.Customers;

namespace Platform.Api.Services;

public interface ICustomerService
{
    Task<IReadOnlyList<CustomerDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<CustomerDto?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<CustomerDto> CreateAsync(CreateCustomerRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    Task<CustomerDto> UpdateAsync(string id, UpdateCustomerRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    Task<CustomerDto> SuspendAsync(string id, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    Task<CustomerDto> ReactivateAsync(string id, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);
}
