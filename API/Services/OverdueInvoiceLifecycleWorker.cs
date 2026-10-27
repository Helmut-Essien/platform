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

        // Invoices that are already Overdue are excluded (P6): they need no transition, and
        // re-processing them every cycle would re-suspend licenses an admin deliberately restored (E8).
        // Auto-suspension therefore only happens in the cycle that moves an invoice to Overdue.
        var invoices = await db.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.DueDate.HasValue
                && x.TotalAmount > 0
                && x.DueDate.Value < now
                && (x.Status == InvoiceStatus.Sent || x.Status == InvoiceStatus.PartiallyPaid))
            .Select(x => new { x.Id, x.LicenseId, x.CustomerId, x.DueDate })
            .ToListAsync(cancellationToken);

        foreach (var invoiceRow in invoices)
        {
            if (!DateTimeNormalizer.IsPastDue(invoiceRow.DueDate, now))
                continue;

            try
            {
                if (!await TryMarkOverdueAsync(db, invoiceRow.Id, now, cancellationToken))
                    continue;

                if (!settings.Value.AutoSuspendOnOverdue || invoiceRow.LicenseId is null)
                    continue;

                var license = await db.Licenses
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(x => x.Id == invoiceRow.LicenseId)
                    .Select(x => new { x.Status })
                    .FirstOrDefaultAsync(cancellationToken);
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

    /// <summary>
    /// Moves a Sent/PartiallyPaid invoice to Overdue under the invoice row lock (B5). The status is
    /// re-checked after the lock is held, so a payment that committed after the candidate scan can no
    /// longer be overwritten with Overdue. Returns false when the invoice is no longer eligible.
    /// </summary>
    internal static async Task<bool> TryMarkOverdueAsync(
        AppDbContext db,
        string invoiceId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var changed = await RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
            if (db.Database.IsRelational())
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""SELECT 1 FROM "Invoices" WHERE "Id" = {invoiceId} FOR UPDATE""",
                    ct);
            }

            var invoice = await db.Invoices
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == invoiceId, ct);
            if (invoice is null
                || invoice.TotalAmount <= 0
                || (invoice.Status != InvoiceStatus.Sent && invoice.Status != InvoiceStatus.PartiallyPaid)
                || !DateTimeNormalizer.IsPastDue(invoice.DueDate, now))
            {
                return false;
            }

            invoice.Status = InvoiceStatus.Overdue;
            invoice.UpdatedAt = now;
            await db.SaveChangesAsync(ct);

            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return true;
        }, cancellationToken);

        db.ChangeTracker.Clear();
        return changed;
    }
}
