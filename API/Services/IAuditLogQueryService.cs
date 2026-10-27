using Platform.Shared.Dtos.Audit;
using Platform.Shared.Dtos.Common;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

/// <summary>Paged, server-side filtered reads of the audit log (E6).</summary>
public interface IAuditLogQueryService
{
    Task<PagedResult<AuditLogDto>> SearchAsync(
        string? customerId = null,
        string? licenseId = null,
        string? invoiceId = null,
        AuditAction? action = null,
        string? search = null,
        int page = 1,
        int pageSize = 25,
        CancellationToken cancellationToken = default);

    Task<AuditLogStatsDto> GetStatsAsync(CancellationToken cancellationToken = default);
}
