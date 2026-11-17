namespace Platform.Api.Services.PriceChanges;

/// <summary>
/// Hourly: queues the customer notice for each pending price change whose effective date is within
/// Billing:PriceChangeNoticeDays (sent once per change; the email outbox worker delivers it).
/// </summary>
public sealed class PriceChangeNoticeWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<PriceChangeNoticeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup (migrations, warm-up) finish first.
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IPlanPriceChangeService>();
                var queued = await service.QueueDueNoticesAsync(DateTime.UtcNow, stoppingToken);
                if (queued > 0)
                    logger.LogInformation("Queued {Count} price change notice(s).", queued);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Price change notice scan failed.");
            }

            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
