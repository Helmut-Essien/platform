using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Services.Payments;

namespace Platform.Api.Extensions;

public static class PaymentServiceExtensions
{
    /// <summary>
    /// Registers Paystack. Always safe to call: without Paystack__SecretKey the feature is disabled
    /// (endpoints return 503, UI hides it) and the app still starts.
    /// </summary>
    public static IServiceCollection AddPlatformPayments(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PaystackOptions>(configuration.GetSection(PaystackOptions.SectionName));

        services.AddHttpClient<IPaymentGateway, PaystackGateway>((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<PaystackOptions>>().Value;
                client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 60));
            })
            .RedactLoggedHeaders(_ => true);

        services.AddScoped<IPaymentCheckoutService, PaymentCheckoutService>();
        services.AddHostedService<PaymentReconcileWorker>();
        return services;
    }

    /// <summary>Logs whether online payments are on (never the key itself).</summary>
    public static void LogPaymentsConfiguration(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<PaystackOptions>>().Value;
        if (!string.IsNullOrWhiteSpace(options.SecretKey) && !PaystackOptions.IsValidBaseUrl(options.BaseUrl))
        {
            app.Logger.LogError("Paystack:BaseUrl '{BaseUrl}' is not a valid https URL; online payments are disabled.", options.BaseUrl);
            return;
        }

        if (!options.Enabled)
        {
            app.Logger.LogInformation("Online payments: disabled (Paystack__SecretKey not set).");
            return;
        }

        var key = options.SecretKey!.Trim();
        if (!key.StartsWith("sk_test_", StringComparison.Ordinal) && !key.StartsWith("sk_live_", StringComparison.Ordinal))
            app.Logger.LogWarning("Paystack__SecretKey does not look like a Paystack secret key (sk_test_… / sk_live_…).");

        app.Logger.LogInformation(
            "Online payments: Paystack enabled ({Mode} mode). Webhook: /api/payments/paystack/webhook",
            options.IsTestMode ? "test" : "live");
    }
}
