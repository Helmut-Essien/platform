using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Extensions;
using Platform.Api.Services;
using Platform.Shared.Dtos.Dashboard;

namespace Platform.Api.Controllers;

[ApiController]
[Authorize(Policy = PlatformAuthorizationPolicies.AdminOnly)]
[Route("api/[controller]")]
public class DashboardController(IDashboardService dashboard) : ControllerBase
{
    [HttpGet("stats")]
    public async Task<ActionResult<DashboardStatsDto>> Stats(CancellationToken cancellationToken)
    {
        var stats = await dashboard.GetStatsAsync(cancellationToken);
        return Ok(stats);
    }
}
