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

    /// <summary>
    /// P7: writes several audit entries at once. The default writes them one by one so existing
    /// implementations and test doubles keep working; <see cref="AuditLogService"/> overrides it to add
    /// them all and save once together with any other pending changes.
    /// </summary>
    async Task WriteBatchAsync(IReadOnlyList<AuditEntryRequest> entries, CancellationToken cancellationToken = default)
    {
        foreach (var e in entries)
        {
            await WriteAsync(e.Action, e.PerformedBy, e.CustomerId, e.LicenseId, e.InvoiceId,
                e.DetailsJson, e.IpAddress, cancellationToken);
        }
    }
}

public sealed record AuditEntryRequest(
    AuditAction Action,
    string PerformedBy,
    string? CustomerId = null,
    string? LicenseId = null,
    string? InvoiceId = null,
    string? DetailsJson = null,
    string? IpAddress = null);
