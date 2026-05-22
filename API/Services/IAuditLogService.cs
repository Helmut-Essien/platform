using Platform.Shared.Dtos.Audit;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public interface IAuditLogService
{
    Task<IReadOnlyList<AuditLogDto>> ListAsync(
        string? customerId = null,
        string? licenseId = null,
        AuditAction? action = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task WriteAsync(
        AuditAction action,
        string performedBy,
        string? customerId = null,
        string? licenseId = null,
        string? invoiceId = null,
        string? detailsJson = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default);
}
