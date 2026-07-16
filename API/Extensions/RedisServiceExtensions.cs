using Microsoft.Extensions.Caching.StackExchangeRedis;
using Platform.Api.Configuration;
using Platform.Api.Services;
using StackExchange.Redis;

namespace Platform.Api.Extensions;

public static class RedisServiceExtensions
{
    public static IServiceCollection AddPlatformRedis(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RedisSettings>(configuration.GetSection(RedisSettings.SectionName));

        var redisConnection = RedisConnectionStringNormalizer.Resolve(configuration);
        var options = ConfigurationOptions.Parse(redisConnection);
        options.AbortOnConnectFail = false;

        Console.Error.WriteLine(
            $"[Startup] Redis endpoint(s): {string.Join(", ", options.EndPoints)} " +
            $"(ssl={options.Ssl}, abortOnConnectFail={options.AbortOnConnectFail})");

        services.AddStackExchangeRedisCache(cacheOptions =>
        {
            cacheOptions.ConfigurationOptions = options;
            cacheOptions.InstanceName = "platform:";
        });

        services.AddSingleton<ILicenseDenyListService, RedisLicenseDenyListService>();
        services.AddScoped<ILicenseValidationService, LicenseValidationService>();

        return services;
    }
}
