using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Shared.Enums;

namespace Platform.Api.Services.Renewals;

/// <summary>
/// Creates and sends next-period invoices <see cref="BillingSettings.RenewalInvoiceDaysBefore"/> days before a
/// license expires. One indexed candidate query per batch (no per-license lookups); each invoice is then created
/// in its own short transaction. Overlapping runs (IIS overlapped recycle, two instances) are safe: creation takes
/// a per-license advisory lock and the unique index IX_Invoices_OneRenewalPerPeriod rejects a second invoice.
/// </summary>
public sealed class RenewalInvoiceWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<BillingSettings> settings,
    ILogger<RenewalInvoiceWorker> logger) : BackgroundService
{
    internal const int BatchSize = 100;
    private const int MaxBatchesPerRun = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup (migrations, warm-up) finish first; IIS recycles shouldn't all hit the DB at once.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (settings.CurrentValue.RenewalInvoicesEnabled)
            {
                try
                {
                    await ProcessAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Renewal invoice scan failed.");
                }
            }

            await Task.Delay(TimeSpan.FromMinutes(Math.Max(5, settings.CurrentValue.RenewalPollMinutes)), stoppingToken);
        }
    }

    /// <summary>Returns how many renewal invoices were created.</summary>
    internal async Task<int> ProcessAsync(CancellationToken cancellationToken)
    {
        var options = settings.CurrentValue;
        var created = 0;
        var failed = new HashSet<string>();
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var billing = scope.ServiceProvider.GetRequiredService<IBillingService>();
            var candidates = await FindCandidatesAsync(db, options, DateTime.UtcNow, failed, cancellationToken);
            if (candidates.Count == 0)
                break;

            var progressed = false;
            foreach (var (licenseId, expiresAt) in candidates)
            {
                try
                {
                    if (await billing.CreateRenewalInvoiceAsync(licenseId, expiresAt, BillingService.RenewalActor,
                            cancellationToken: cancellationToken) is not null)
                        created++;
                    else
                        failed.Add(licenseId); // skipped (e.g. blocked by an open earlier invoice): don't re-scan it this run
                    progressed = true;
                }
                catch (ConflictException)
                {
                    progressed = true; // another run created it first
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed.Add(licenseId);
                    logger.LogError(ex, "Creating the renewal invoice for license {LicenseId} failed.", licenseId);
                }
                db.ChangeTracker.Clear();
            }

            if (candidates.Count < BatchSize || !progressed)
                break;
        }

        if (created > 0)
            logger.LogInformation("Created {Count} renewal invoice(s).", created);
        return created;
    }

    /// <summary>
    /// Active/expired, priced, opted-in licenses of non-suspended customers whose expiry is inside the window and
    /// that have no renewal invoice (any status, so a voided one isn't re-created) for that expiry yet.
    /// Uses IX_Licenses_Status_ExpiresAt and the Invoices.LicenseId index.
    /// </summary>
    internal static async Task<List<(string Id, DateTime ExpiresAt)>> FindCandidatesAsync(
        AppDbContext db, BillingSettings options, DateTime now, IReadOnlyCollection<string> exclude, CancellationToken cancellationToken)
    {
        var horizon = now.AddDays(Math.Clamp(options.RenewalInvoiceDaysBefore, 1, 90));
        var lookback = now.AddDays(-Math.Clamp(options.RenewalLookbackDays, 0, 365));
        var rows = await db.Licenses
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(l => (l.Status == LicenseStatus.Active || l.Status == LicenseStatus.Expired)
                && l.ExpiresAt != null
                && l.ExpiresAt <= horizon
                && l.ExpiresAt > lookback
                && l.AutoRenewInvoice
                && l.BillingAmount > 0
                && l.BillingCurrency != null
                && !l.Customer.IsSuspended
                && !exclude.Contains(l.Id)
                && !db.Invoices.IgnoreQueryFilters().Any(i => i.LicenseId == l.Id && i.RenewalForExpiresAt == l.ExpiresAt))
            .OrderBy(l => l.ExpiresAt)
            .ThenBy(l => l.Id)
            .Take(BatchSize)
            .Select(l => new { l.Id, l.ExpiresAt })
            .ToListAsync(cancellationToken);
        return rows.Select(x => (x.Id, x.ExpiresAt!.Value)).ToList();
    }
}
