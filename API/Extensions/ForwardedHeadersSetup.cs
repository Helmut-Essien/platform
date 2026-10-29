using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Platform.Api.Extensions;

/// <summary>
/// Config-driven forwarded-header trust. Disabled by default so clients cannot spoof
/// X-Forwarded-For (which feeds rate-limit partitions) when no proxy is in front.
/// </summary>
public static class ForwardedHeadersSetup
{
    public const string SectionName = "ForwardedHeaders";

    /// <returns>True when forwarded headers middleware should run.</returns>
    public static bool Configure(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var enabled = section.GetValue("Enabled", false);
        var proxies = ParseProxies(section.GetSection("KnownProxies").Get<string[]>());
        var networks = ParseNetworks(section.GetSection("KnownNetworks").Get<string[]>());

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = section.GetValue("ForwardLimit", 1);
            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            foreach (var proxy in proxies)
                options.KnownProxies.Add(proxy);
            foreach (var network in networks)
                options.KnownNetworks.Add(network);
            // Loopback stays trusted when nothing is configured (ASP.NET Core default).
            if (proxies.Count == 0 && networks.Count == 0)
                options.KnownProxies.Add(IPAddress.Loopback);
        });

        return enabled;
    }

    internal static List<IPAddress> ParseProxies(string[]? values) =>
        (values ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => IPAddress.TryParse(v.Trim(), out var ip)
                ? ip
                : throw new InvalidOperationException($"ForwardedHeaders:KnownProxies entry '{v}' is not an IP address."))
            .ToList();

    internal static List<Microsoft.AspNetCore.HttpOverrides.IPNetwork> ParseNetworks(string[]? values) =>
        (values ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v =>
            {
                var parts = v.Trim().Split('/', 2);
                if (parts.Length == 2
                    && IPAddress.TryParse(parts[0], out var prefix)
                    && int.TryParse(parts[1], out var length))
                    return new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length);
                throw new InvalidOperationException($"ForwardedHeaders:KnownNetworks entry '{v}' must be CIDR, e.g. 10.0.0.0/8.");
            })
            .ToList();
}
