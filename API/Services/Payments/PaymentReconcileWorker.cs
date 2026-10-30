using Microsoft.Extensions.Options;
using Platform.Api.Configuration;

namespace Platform.Api.Services.Payments;

/// <summary>
/// Safety net for missed or failed webhooks: re-verifies Paystack checkouts that are still pending
/// after 2 minutes and expires those older than 24 hours. Idle (one cheap check) when Paystack is off.
/// </summary>
public sealed class PaymentReconcileWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<PaystackOptions> options,
    ILogger<PaymentReconcileWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup (migrations, seeding) finish first.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (options.Value.Enabled)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var checkouts = scope.ServiceProvider.GetRequiredService<IPaymentCheckoutService>();
                    var count = await checkouts.ReconcileAsync(stoppingToken);
                    if (count > 0)
                        logger.LogInformation("Paystack reconcile checked {Count} pending checkout(s).", count);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Paystack reconcile scan failed.");
                }
            }

            await Task.Delay(
                TimeSpan.FromMinutes(Math.Clamp(options.Value.ReconcileIntervalMinutes, 1, 60)),
                stoppingToken);
        }
    }
}
