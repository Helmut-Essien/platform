using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Hosting;
using Platform.Api.Configuration;
using Platform.Api.Services;
using StackExchange.Redis;

namespace Platform.Api.Extensions;

public static class RedisServiceExtensions
{
    public static IServiceCollection AddPlatformRedis(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.Configure<RedisSettings>(configuration.GetSection(RedisSettings.SectionName));

        var redisConnection = RedisConnectionStringNormalizer.TryResolve(configuration);
        if (redisConnection is null)
        {
            // Single-instance hosting (e.g. IIS / MonsterASP): validation cache and deny lists
            // live in process memory. Not shared across instances and lost on app restart;
            // the database remains the source of truth for license status.
            services.AddDistributedMemoryCache();
            services.AddHostedService<InMemoryCacheWarning>();
        }
        else
        {
            var options = ConfigurationOptions.Parse(redisConnection);
            options.AbortOnConnectFail = false;

            if (environment.IsDevelopment())
            {
                Console.Error.WriteLine(
                    $"[Startup] Redis endpoint(s): {string.Join(", ", options.EndPoints)} " +
                    $"(ssl={options.Ssl}, abortOnConnectFail={options.AbortOnConnectFail})");
            }

            services.AddStackExchangeRedisCache(cacheOptions =>
            {
                cacheOptions.ConfigurationOptions = options;
                cacheOptions.InstanceName = "platform:";
            });
        }

        services.AddSingleton<ILicenseDenyListService, RedisLicenseDenyListService>();
        services.AddScoped<ILicenseValidationService, LicenseValidationService>();

        return services;
    }
}

internal sealed class InMemoryCacheWarning(ILogger<InMemoryCacheWarning> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "No Redis connection string configured (Redis:ConnectionString / REDIS_URL). " +
            "Using in-memory validation cache and deny lists; run a single app instance only.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
