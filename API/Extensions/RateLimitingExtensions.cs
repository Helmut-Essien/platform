using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Security;

namespace Platform.Api.Extensions;

public static class RateLimitingExtensions
{
    public const string LicenseValidatePolicy = "license-validate";
    public const string AuthLoginPolicy = "auth-login";
    public const string PaymentWebhookPolicy = "payment-webhook";
    public const string PublicPayPolicy = "public-pay";

    public static IServiceCollection AddPlatformRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RateLimitingSettings>(configuration.GetSection(RateLimitingSettings.SectionName));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, _) =>
            {
                context.HttpContext.Response.ContentType = "application/json";
                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    message = "Too many requests. Try again later."
                });
            };

            options.AddPolicy<string>(LicenseValidatePolicy, httpContext =>
            {
                var settings = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingSettings>>().Value;
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var integrationKey = httpContext.Request.Headers["X-Integration-Key"].FirstOrDefault();
                var integrationPart = string.IsNullOrWhiteSpace(integrationKey)
                    ? "none"
                    : KeyLookupHasher.ComputeSha256Hex(integrationKey)[..16];
                var partitionKey = $"{ip}:{integrationPart}";

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.LicenseValidatePermitLimit,
                        Window = TimeSpan.FromMinutes(settings.LicenseValidateWindowMinutes),
                        QueueLimit = 0
                    });
            });

            options.AddPolicy<string>(AuthLoginPolicy, httpContext =>
            {
                var settings = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingSettings>>().Value;
                return RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.AuthLoginPermitLimit,
                        Window = TimeSpan.FromMinutes(settings.AuthLoginWindowMinutes),
                        QueueLimit = 0
                    });
            });

            // Generous: Paystack retries bursts; this only stops floods of forged (unsigned) requests.
            options.AddPolicy<string>(PaymentWebhookPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 120,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    }));

            // Anonymous pay page: a real payer needs a handful of calls (summary, checkout, a few status polls).
            options.AddPolicy<string>(PublicPayPolicy, httpContext =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 40,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 4,
                        QueueLimit = 0
                    }));
        });

        return services;
    }
}
