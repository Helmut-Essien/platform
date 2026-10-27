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

    /// <summary>
    /// Adds an audit entry and calls SaveChanges on the shared context, so any other pending changes are
    /// persisted in the same unit of work (one round trip, atomic with the audited change).
    /// </summary>
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
