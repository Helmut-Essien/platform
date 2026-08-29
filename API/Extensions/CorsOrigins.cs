namespace Platform.Api.Extensions;

public static class CorsOrigins
{
    public static string[] Resolve(IConfiguration configuration)
    {
        var origins = (configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .ToList();

        var envOrigins = Environment.GetEnvironmentVariable("CORS_ORIGINS");
        if (!string.IsNullOrWhiteSpace(envOrigins))
        {
            origins.AddRange(envOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return origins.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
