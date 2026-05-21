using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public class AuditLogService(AppDbContext db) : IAuditLogService
{
    public async Task WriteAsync(
        AuditAction action,
        string performedBy,
        string? customerId = null,
        string? licenseId = null,
        string? invoiceId = null,
        string? detailsJson = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Action = action,
            PerformedBy = performedBy,
            CustomerId = customerId,
            LicenseId = licenseId,
            InvoiceId = invoiceId,
            DetailsJson = detailsJson,
            IpAddress = ipAddress,
            Timestamp = DateTime.UtcNow
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}
