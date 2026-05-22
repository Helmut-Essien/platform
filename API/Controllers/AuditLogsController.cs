using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Extensions;
using Platform.Api.Services;
using Platform.Shared.Dtos.Audit;
using Platform.Shared.Enums;

namespace Platform.Api.Controllers;

[ApiController]
[Authorize(Policy = PlatformAuthorizationPolicies.AdminOnly)]
[Route("api/audit-logs")]
public class AuditLogsController(IAuditLogService auditLogs) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AuditLogDto>>> List(
        [FromQuery] string? customerId,
        [FromQuery] string? licenseId,
        [FromQuery] AuditAction? action,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var logs = await auditLogs.ListAsync(customerId, licenseId, action, limit, cancellationToken);
        return Ok(logs);
    }
}
