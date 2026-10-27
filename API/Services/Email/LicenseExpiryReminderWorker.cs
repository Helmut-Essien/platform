using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
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

        foreach (var license in licenses)
        {
            var expiresAt = license.ExpiresAt!.Value;
            var remaining = expiresAt - now;
            // Tightest step whose window we are inside.
            var step = steps.Where(s => remaining <= TimeSpan.FromDays(s)).Min();
            var windowOpened = expiresAt.AddDays(-step);
            if (lastReminders.TryGetValue(license.Id, out var last) && last >= windowOpened)
                continue;

            var template = templates.ExpiryReminder(license.Customer, license.ServiceProduct, license);
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
