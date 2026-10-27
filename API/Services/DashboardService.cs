using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Shared.Dtos.Dashboard;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public class DashboardService(AppDbContext db) : IDashboardService
{
    public async Task<DashboardStatsDto> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var threshold = DateTime.UtcNow.AddDays(30);

        var customerCount = await db.Customers.CountAsync(cancellationToken);

        // E1: license KPIs use the same default query filter as the Licenses list the KPI links to
        // (licenses of suspended customers are excluded), so the number matches what the user sees.
        var activeLicenses = await db.Licenses
            .CountAsync(l => l.Status == LicenseStatus.Active, cancellationToken);

        var expiringWithin30Days = await db.Licenses
            .CountAsync(
                l => l.Status == LicenseStatus.Active
                    && l.ExpiresAt.HasValue
                    && l.ExpiresAt.Value <= threshold,
                cancellationToken);

        var unpaidInvoices = await db.Invoices
            .IgnoreQueryFilters()
            .CountAsync(
                i => i.Status == InvoiceStatus.Sent
                    || i.Status == InvoiceStatus.PartiallyPaid
                    || i.Status == InvoiceStatus.Overdue,
                cancellationToken);

        return new DashboardStatsDto
        {
            CustomerCount = customerCount,
            ActiveLicenses = activeLicenses,
            ExpiringWithin30Days = expiringWithin30Days,
            UnpaidInvoices = unpaidInvoices
        };
    }
}
