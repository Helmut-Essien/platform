using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Helpers;
using Platform.Shared.Dtos.Audit;
using Platform.Shared.Dtos.Common;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public class AuditLogService(AppDbContext db) : IAuditLogService, IAuditLogQueryService
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

        return await MapWithNamesAsync(logs, cancellationToken);
    }

    internal static readonly AuditAction[] SecurityActions =
    [
        AuditAction.LicenseRevoked,
        AuditAction.LicenseSuspended,
        AuditAction.CustomerSuspended,
        AuditAction.IntegrationKeyRevoked,
        AuditAction.InvoiceVoided
    ];

    internal static readonly AuditAction[] CriticalActions =
    [
        AuditAction.LicenseRevoked,
        AuditAction.IntegrationKeyRevoked
    ];

    public async Task<PagedResult<AuditLogDto>> SearchAsync(
        string? customerId = null,
        string? licenseId = null,
        string? invoiceId = null,
        AuditAction? action = null,
        string? search = null,
        int page = 1,
        int pageSize = PagingHelper.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var (normalizedPage, normalizedPageSize, skip) = PagingHelper.Normalize(page, pageSize);
        var query = db.AuditLogs.AsNoTracking().AsQueryable();

        if (customerId is not null)
            query = query.Where(a => a.CustomerId == customerId);
        if (licenseId is not null)
            query = query.Where(a => a.LicenseId == licenseId);
        if (invoiceId is not null)
            query = query.Where(a => a.InvoiceId == invoiceId);
        if (action is not null)
            query = query.Where(a => a.Action == action);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            // Action names are matched the way the UI shows them (LICENSE_ISSUED / "license issued").
            var squashed = term.Replace("_", "").Replace(" ", "");
            var matchingActions = Enum.GetValues<AuditAction>()
                .Where(a => a.ToString().ToLower().Contains(squashed))
                .ToList();
            var matchingCustomerIds = await db.Customers
                .AsNoTracking()
                .Where(c => c.Name.ToLower().Contains(term))
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);

            query = query.Where(a =>
                matchingActions.Contains(a.Action)
                || a.PerformedBy.ToLower().Contains(term)
                || (a.IpAddress != null && a.IpAddress.ToLower().Contains(term))
                || (a.DetailsJson != null && a.DetailsJson.ToLower().Contains(term))
                || (a.CustomerId != null && matchingCustomerIds.Contains(a.CustomerId)));
        }

        var ordered = query.OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id);
        var total = await ordered.CountAsync(cancellationToken);
        var logs = await ordered.Skip(skip).Take(normalizedPageSize).ToListAsync(cancellationToken);

        return new PagedResult<AuditLogDto>
        {
            Items = await MapWithNamesAsync(logs, cancellationToken),
            TotalCount = total,
            Page = normalizedPage,
            PageSize = normalizedPageSize
        };
    }

    public async Task<AuditLogStatsDto> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var todayStart = DateTime.UtcNow.Date;
        var byAction = await db.AuditLogs
            .AsNoTracking()
            .GroupBy(a => a.Action)
            .Select(g => new { Action = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var today = await db.AuditLogs
            .AsNoTracking()
            .CountAsync(a => a.Timestamp >= todayStart, cancellationToken);

        return new AuditLogStatsDto
        {
            TotalCount = byAction.Sum(x => (long)x.Count),
            TodayCount = today,
            SecurityFlagCount = byAction.Where(x => SecurityActions.Contains(x.Action)).Sum(x => (long)x.Count),
            CriticalFlagCount = byAction.Where(x => CriticalActions.Contains(x.Action)).Sum(x => (long)x.Count)
        };
    }

    /// <summary>Maps entries and resolves customer / license display names with two small queries (no N+1).</summary>
    private async Task<List<AuditLogDto>> MapWithNamesAsync(List<AuditLog> logs, CancellationToken cancellationToken)
    {
        var customerIds = logs.Where(l => l.CustomerId != null).Select(l => l.CustomerId!).Distinct().ToList();
        var licenseIds = logs.Where(l => l.LicenseId != null).Select(l => l.LicenseId!).Distinct().ToList();

        var customerNames = customerIds.Count == 0
            ? []
            : await db.Customers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        var licenseLabels = licenseIds.Count == 0
            ? []
            : (await db.Licenses.IgnoreQueryFilters().AsNoTracking()
                .Where(l => licenseIds.Contains(l.Id))
                .Select(l => new { l.Id, CustomerName = l.Customer.Name, Code = l.ServiceProduct.Code })
                .ToListAsync(cancellationToken))
                .ToDictionary(l => l.Id, l => $"{l.CustomerName} / {l.Code}");

        return logs.Select(l =>
        {
            var dto = Map(l);
            dto.CustomerName = l.CustomerId is not null ? customerNames.GetValueOrDefault(l.CustomerId) : null;
            dto.LicenseLabel = l.LicenseId is not null ? licenseLabels.GetValueOrDefault(l.LicenseId) : null;
            return dto;
        }).ToList();
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

    public async Task WriteBatchAsync(
        IReadOnlyList<AuditEntryRequest> entries,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        db.AuditLogs.AddRange(entries.Select(e => new AuditLog
        {
            Action = e.Action,
            PerformedBy = e.PerformedBy,
            CustomerId = e.CustomerId,
            LicenseId = e.LicenseId,
            InvoiceId = e.InvoiceId,
            DetailsJson = e.DetailsJson,
            IpAddress = e.IpAddress,
            Timestamp = now
        }));

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
