using System.Text;
using StackExchange.Redis;

namespace Platform.Api.Configuration;

/// <summary>
/// Normalizes Redis connection values from appsettings or Render-style REDIS_URL
/// into a StackExchange.Redis configuration string.
/// </summary>
public static class RedisConnectionStringNormalizer
{
    public static string Resolve(IConfiguration configuration)
    {
        var raw = configuration["Redis:ConnectionString"]
            ?? configuration["REDIS_URL"]
            ?? Environment.GetEnvironmentVariable("REDIS_URL")
            ?? "localhost:6379";

        return Normalize(raw);
    }

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "localhost:6379,abortConnect=false";

        raw = raw.Trim();

        if (raw.StartsWith("redis://", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
        {
            return FromUri(raw);
        }

        return EnsureAbortConnectFalse(raw);
    }

    public static ConfigurationOptions ToConfigurationOptions(string? raw)
    {
        var normalized = Normalize(raw);
        var options = ConfigurationOptions.Parse(normalized);
        options.AbortOnConnectFail = false;
        return options;
    }

    private static string FromUri(string raw)
    {
        var uri = new Uri(raw);
        var useSsl = raw.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase);
        var host = uri.Host;
        var port = uri.IsDefaultPort ? (useSsl ? 6380 : 6379) : uri.Port;

        string? user = null;
        string? password = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            if (parts.Length == 2)
            {
                user = Uri.UnescapeDataString(parts[0]);
                password = Uri.UnescapeDataString(parts[1]);
            }
            else
            {
                password = Uri.UnescapeDataString(parts[0]);
            }
        }

        var sb = new StringBuilder();
        sb.Append(host).Append(':').Append(port);
        sb.Append(",abortConnect=false");

        if (!string.IsNullOrEmpty(user))
            sb.Append(",user=").Append(user);

        if (!string.IsNullOrEmpty(password))
            sb.Append(",password=").Append(password);

        if (useSsl)
            sb.Append(",ssl=True");

        return sb.ToString();
    }

    private static string EnsureAbortConnectFalse(string raw)
    {
        if (raw.Contains("abortConnect", StringComparison.OrdinalIgnoreCase))
            return raw;

        return raw.TrimEnd(',') + ",abortConnect=false";
    }
}
