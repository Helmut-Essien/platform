using Microsoft.Extensions.Caching.StackExchangeRedis;
using Platform.Api.Configuration;
using Platform.Api.Services;

namespace Platform.Api.Extensions;

public static class RedisServiceExtensions
{
    public static IServiceCollection AddPlatformRedis(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RedisSettings>(configuration.GetSection(RedisSettings.SectionName));

        var redisConnection = configuration.GetSection(RedisSettings.SectionName)
            .GetValue<string>("ConnectionString") ?? "localhost:6379";

        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnection;
            options.InstanceName = "platform:";
        });

        services.AddSingleton<ILicenseDenyListService, RedisLicenseDenyListService>();
        services.AddScoped<ILicenseValidationService, LicenseValidationService>();

        return services;
    }
}
