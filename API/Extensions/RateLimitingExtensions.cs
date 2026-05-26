using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;

namespace Platform.Api.Extensions;

public static class RateLimitingExtensions
{
    public const string LicenseValidatePolicy = "license-validate";

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
                return RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = settings.LicenseValidatePermitLimit,
                        Window = TimeSpan.FromMinutes(settings.LicenseValidateWindowMinutes),
                        QueueLimit = 0
                    });
            });
        });

        return services;
    }
}
