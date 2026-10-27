using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Extensions;
using Platform.Api.Services;
using Platform.Shared.Dtos.Audit;
using Platform.Shared.Dtos.Common;
using Platform.Shared.Enums;

namespace Platform.Api.Controllers;

[ApiController]
[Authorize(Policy = PlatformAuthorizationPolicies.AdminOnly)]
[Route("api/audit-logs")]
public class AuditLogsController(IAuditLogService auditLogs, IAuditLogQueryService auditQuery) : ControllerBase
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

    /// <summary>Server-side filtered and paged audit log (E6). The unpaged list above is unchanged.</summary>
    [HttpGet("paged")]
    public async Task<ActionResult<PagedResult<AuditLogDto>>> Paged(
        [FromQuery] string? customerId,
        [FromQuery] string? licenseId,
        [FromQuery] string? invoiceId,
        [FromQuery] AuditAction? action,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default) =>
        Ok(await auditQuery.SearchAsync(customerId, licenseId, invoiceId, action, search, page, pageSize, cancellationToken));

    /// <summary>Counts over the whole audit log.</summary>
    [HttpGet("stats")]
    public async Task<ActionResult<AuditLogStatsDto>> Stats(CancellationToken cancellationToken) =>
        Ok(await auditQuery.GetStatsAsync(cancellationToken));
}
