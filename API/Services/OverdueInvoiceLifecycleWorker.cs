using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Helpers;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public class OverdueInvoiceLifecycleWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<LifecycleSettings> settings,
    ILogger<OverdueInvoiceLifecycleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Overdue invoice lifecycle scan failed.");
            }

            await Task.Delay(
                TimeSpan.FromMinutes(Math.Max(5, settings.Value.OverduePollMinutes)),
                stoppingToken);
        }
    }

    internal async Task ProcessAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var licenseService = scope.ServiceProvider.GetRequiredService<ILicenseService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var now = DateTime.UtcNow;

        try
        {
            await licenseService.ExpireDueLicensesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "License expiry transition failed.");
        }

        var invoices = await db.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.DueDate.HasValue
                && x.TotalAmount > 0
                && x.DueDate.Value < now
                && (x.Status == InvoiceStatus.Sent
                    || x.Status == InvoiceStatus.PartiallyPaid
                    || x.Status == InvoiceStatus.Overdue))
            .Select(x => new { x.Id, x.LicenseId, x.CustomerId, x.Status, x.DueDate, x.TotalAmount })
            .ToListAsync(cancellationToken);

        foreach (var invoiceRow in invoices)
        {
            if (!DateTimeNormalizer.IsPastDue(invoiceRow.DueDate, now))
                continue;

            try
            {
                if (invoiceRow.Status != InvoiceStatus.Overdue)
                {
                    var invoice = await db.Invoices
                        .IgnoreQueryFilters()
                        .FirstAsync(x => x.Id == invoiceRow.Id, cancellationToken);
                    if (invoice.TotalAmount <= 0)
                        continue;
                    invoice.Status = InvoiceStatus.Overdue;
                    invoice.UpdatedAt = now;
                    await db.SaveChangesAsync(cancellationToken);
                    db.ChangeTracker.Clear();
                }

                if (!settings.Value.AutoSuspendOnOverdue || invoiceRow.LicenseId is null)
                    continue;

                var license = await db.Licenses
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == invoiceRow.LicenseId, cancellationToken);
                if (license is null || license.Status != LicenseStatus.Active)
                    continue;

                await licenseService.SuspendAsync(
                    invoiceRow.LicenseId,
                    "system:overdue-invoice",
                    cancellationToken: cancellationToken,
                    notificationReason: "Access was suspended because a linked invoice is overdue.",
                    autoSuspendedForOverdueInvoiceId: invoiceRow.Id);
                await audit.WriteAsync(
                    AuditAction.LicenseAutoSuspendedOverdue,
                    "system:overdue-invoice",
                    invoiceRow.CustomerId,
                    invoiceRow.LicenseId,
                    invoiceRow.Id,
                    AuditJson.Serialize(new { invoiceId = invoiceRow.Id }),
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Overdue processing failed for invoice {InvoiceId}.", invoiceRow.Id);
                db.ChangeTracker.Clear();
            }
        }
    }
}
