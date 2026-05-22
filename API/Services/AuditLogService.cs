using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Shared.Dtos.Audit;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public class AuditLogService(AppDbContext db) : IAuditLogService
{
    public async Task<IReadOnlyList<AuditLogDto>> ListAsync(
        string? customerId = null,
        string? licenseId = null,
        AuditAction? action = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var query = db.AuditLogs.AsNoTracking().AsQueryable();

        if (customerId is not null)
            query = query.Where(a => a.CustomerId == customerId);

        if (licenseId is not null)
            query = query.Where(a => a.LicenseId == licenseId);

        if (action is not null)
            query = query.Where(a => a.Action == action);

        var logs = await query
            .OrderByDescending(a => a.Timestamp)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(cancellationToken);

        return logs.Select(Map).ToList();
    }

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

    private static AuditLogDto Map(AuditLog log) => new()
    {
        Id = log.Id,
        CustomerId = log.CustomerId,
        LicenseId = log.LicenseId,
        InvoiceId = log.InvoiceId,
        Action = log.Action,
        PerformedBy = log.PerformedBy,
        DetailsJson = log.DetailsJson,
        IpAddress = log.IpAddress,
        Timestamp = log.Timestamp
    };
}
