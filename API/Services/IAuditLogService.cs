using Platform.Shared.Enums;

namespace Platform.Api.Services;

public interface IAuditLogService
{
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
