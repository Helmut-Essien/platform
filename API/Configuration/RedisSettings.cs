namespace Platform.Api.Configuration;

public class RedisSettings
{
    public const string SectionName = "Redis";

    public string ConnectionString { get; set; } = "localhost:6379";

    public int ValidationCacheSeconds { get; set; } = 60;

    public int IntegrationKeyLastUsedUpdateMinutes { get; set; } = 15;
}
