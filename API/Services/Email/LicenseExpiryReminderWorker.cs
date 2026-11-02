using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Entities;
using Platform.Api.Data;
using Platform.Shared.Enums;

namespace Platform.Api.Services.Email;

public class LicenseExpiryReminderWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<LifecycleSettings> settings,
    ILogger<LicenseExpiryReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (settings.Value.ExpiryReminderEnabled)
            {
                try
                {
                    await EnqueueRemindersAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "License expiry reminder scan failed.");
                }
            }

            await Task.Delay(
                TimeSpan.FromHours(Math.Max(1, settings.Value.ExpiryReminderPollHours)),
                stoppingToken);
        }
    }

    internal async Task EnqueueRemindersAsync(CancellationToken cancellationToken)
    {
        await EnqueueExpiryRemindersAsync(cancellationToken);
        try
        {
            await EnqueueTrialRemindersAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another instance updated one of these licenses at the same moment; the next scan retries.
        }
    }

    /// <summary>
    /// Trial-ending reminder for trials that won't get a trial invoice (no price, automatic invoices off for the
    /// license, or renewal invoices switched off). Priced trials get the reminder with their first invoice from
    /// the renewal worker. Sent once per trial end (<c>TrialReminderForEndsAt</c>), so an extended trial is
    /// reminded again; the license's concurrency token stops two overlapping scans sending it twice.
    /// Uses the IX_Licenses_Trial_ExpiresAt partial index.
    /// </summary>
    internal async Task<int> EnqueueTrialRemindersAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IEmailOutboxService>();
        var templates = scope.ServiceProvider.GetRequiredService<EmailTemplateService>();
        var billing = scope.ServiceProvider.GetService<IOptionsMonitor<BillingSettings>>()?.CurrentValue ?? new BillingSettings();
        var now = DateTime.UtcNow;
        var horizon = now.AddDays(Math.Clamp(billing.TrialReminderDaysBefore, 1, 60));
        var invoicesOn = billing.RenewalInvoicesEnabled;

        var trials = await db.Licenses
            .IgnoreQueryFilters()
            .Include(x => x.Customer)
            .Include(x => x.ServiceProduct)
            .Where(x => x.IsTrial
                && x.Status == LicenseStatus.Active
                && !x.Customer.IsSuspended
                && x.ExpiresAt != null
                && x.ExpiresAt <= horizon
                && x.ExpiresAt >= now.Date // ended trials (not yet flipped to Expired) don't fill the batch
                && (x.TrialReminderForEndsAt == null || x.TrialReminderForEndsAt != x.ExpiresAt)
                && !(invoicesOn && x.AutoRenewInvoice && x.BillingAmount > 0 && x.BillingCurrency != null))
            .OrderBy(x => x.ExpiresAt)
            .Take(500)
            .ToListAsync(cancellationToken);

        var audits = new List<AuditEntryRequest>();
        foreach (var license in trials)
        {
            if (Helpers.DateTimeNormalizer.IsPastExpiry(license.ExpiresAt, now))
                continue;
            var template = templates.TrialEndingReminder(license.Customer, license.ServiceProduct, license);
            outbox.Enqueue(
                EmailDeliveryKind.TrialReminder,
                CustomerContactResolver.Technical(license.Customer),
                template.Subject,
                template.Html,
                license.CustomerId,
                license.Id);
            license.TrialReminderForEndsAt = license.ExpiresAt;
            license.UpdatedAt = now;
            audits.Add(new AuditEntryRequest(AuditAction.LicenseTrialReminderQueued, "system:trial-reminder",
                license.CustomerId, license.Id, null,
                Helpers.AuditJson.Serialize(new { trialEndsAt = license.ExpiresAt, invoice = false })));
        }

        if (audits.Count == 0)
            return 0;
        // Save first: if another scan won the race (concurrency token), nothing is audited for a reminder not sent.
        await db.SaveChangesAsync(cancellationToken);
        await scope.ServiceProvider.GetRequiredService<IAuditLogService>().WriteBatchAsync(audits, cancellationToken);
        return audits.Count;
    }

    private async Task EnqueueExpiryRemindersAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var outbox = scope.ServiceProvider.GetRequiredService<IEmailOutboxService>();
        var templates = scope.ServiceProvider.GetRequiredService<EmailTemplateService>();
        var now = DateTime.UtcNow;
        // E5: stepped reminders (e.g. 30, 7 and 1 days before expiry). A reminder for a step is sent at
        // most once per expiry date: it is skipped when one was already queued since that step's window opened.
        var steps = ReminderSteps(settings.Value);
        var threshold = now.AddDays(steps[0]);

        var licenses = await db.Licenses
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(x => x.Customer)
            .Include(x => x.ServiceProduct)
            .Where(x => x.Status == LicenseStatus.Active
                && !x.IsTrial // trials get their own reminder (EnqueueTrialRemindersAsync / the trial invoice)
                && !x.Customer.IsSuspended
                && x.ExpiresAt.HasValue
                && x.ExpiresAt.Value > now
                && x.ExpiresAt.Value <= threshold)
            .ToListAsync(cancellationToken);

        if (licenses.Count == 0)
            return;

        // P8: one batched lookup of the latest reminder per license instead of an AnyAsync per license.
        var licenseIds = licenses.Select(x => x.Id).ToList();
        var lastReminders = await db.EmailOutboxMessages
            .AsNoTracking()
            .Where(x => x.Kind == EmailDeliveryKind.ExpiryReminder
                && x.Status != EmailDeliveryStatus.Cancelled
                && x.LicenseId != null
                && licenseIds.Contains(x.LicenseId))
            .GroupBy(x => x.LicenseId!)
            .Select(g => new { LicenseId = g.Key, LastCreatedAt = g.Max(m => m.CreatedAt) })
            .ToDictionaryAsync(x => x.LicenseId, x => x.LastCreatedAt, cancellationToken);

        // Open renewal invoices for these licenses (one query), so the reminder can carry the pay button.
        var payLinks = scope.ServiceProvider.GetService<Renewals.PayLinkService>();
        var openInvoices = await db.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(i => i.Receipts)
            .Where(i => i.LicenseId != null
                && licenseIds.Contains(i.LicenseId)
                && i.RenewalForExpiresAt != null
                && i.TotalAmount > 0
                && (i.Status == InvoiceStatus.Sent || i.Status == InvoiceStatus.Overdue || i.Status == InvoiceStatus.PartiallyPaid))
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(cancellationToken);
        var openByLicense = openInvoices
            .GroupBy(i => i.LicenseId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var license in licenses)
        {
            var expiresAt = license.ExpiresAt!.Value;
            var remaining = expiresAt - now;
            // Tightest step whose window we are inside.
            var step = steps.Where(s => remaining <= TimeSpan.FromDays(s)).Min();
            var windowOpened = expiresAt.AddDays(-step);
            if (lastReminders.TryGetValue(license.Id, out var last) && last >= windowOpened)
                continue;

            Invoice? openInvoice = null;
            if (openByLicense.TryGetValue(license.Id, out var candidates))
                openInvoice = candidates.FirstOrDefault(i => i.RenewalForExpiresAt == license.ExpiresAt) ?? candidates[0];
            var payUrl = openInvoice is null ? null : payLinks?.BuildUrl(openInvoice, now);
            var template = templates.ExpiryReminder(license.Customer, license.ServiceProduct, license, openInvoice, payUrl);
            outbox.Enqueue(
                EmailDeliveryKind.ExpiryReminder,
                CustomerContactResolver.Technical(license.Customer),
                template.Subject,
                template.Html,
                license.CustomerId,
                license.Id);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Distinct reminder steps in days, descending; the first is the scan horizon.</summary>
    internal static int[] ReminderSteps(LifecycleSettings settings)
    {
        var max = Math.Max(1, settings.ExpiryReminderDays);
        return new[] { max }
            .Concat(settings.ExpiryReminderAdditionalSteps ?? [])
            .Where(d => d >= 1 && d <= max)
            .Distinct()
            .OrderDescending()
            .ToArray();
    }
}
