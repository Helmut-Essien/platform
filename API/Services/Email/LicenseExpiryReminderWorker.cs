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
        var threshold = now.AddDays(Math.Max(1, settings.Value.ExpiryReminderDays));
        var dedupeAfter = now.AddDays(-Math.Max(1, settings.Value.ExpiryReminderDays));

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

        foreach (var license in licenses)
        {
            var alreadyQueued = await db.EmailOutboxMessages.AnyAsync(
                x => x.Kind == EmailDeliveryKind.ExpiryReminder
                    && x.LicenseId == license.Id
                    && x.CreatedAt >= dedupeAfter,
                cancellationToken);
            if (alreadyQueued)
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
}
