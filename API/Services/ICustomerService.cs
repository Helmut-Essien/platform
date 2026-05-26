using Platform.Shared.Dtos.Common;
using Platform.Shared.Dtos.Customers;

namespace Platform.Api.Services;

public interface ICustomerService
{
    Task<PagedResult<CustomerDto>> ListAsync(int page = 1, int pageSize = 25, CancellationToken cancellationToken = default);

    Task<CustomerDto?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<CustomerDto> CreateAsync(CreateCustomerRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    Task<CustomerDto> UpdateAsync(string id, UpdateCustomerRequest request, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    Task<CustomerDto> SuspendAsync(string id, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);

    Task<CustomerDto> ReactivateAsync(string id, string performedBy, string? ipAddress = null, CancellationToken cancellationToken = default);
}
