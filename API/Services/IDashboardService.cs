using Platform.Shared.Dtos.Dashboard;

namespace Platform.Api.Services;

public interface IDashboardService
{
    Task<DashboardStatsDto> GetStatsAsync(CancellationToken cancellationToken = default);
}
