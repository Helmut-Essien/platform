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
            // Never trust an unbounded X-Forwarded-For chain: 1 hop by default, at most 5.
            options.ForwardLimit = Math.Clamp(section.GetValue("ForwardLimit", 1), 1, 5);
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

    private static int _proxyWarningLogged;

    /// <summary>
    /// Logs once when requests look proxied but forwarded headers are off (every client would share the proxy's
    /// address, so per-IP rate limits would throttle all customers together), or when they are on but the
    /// header comes from an untrusted hop. Never changes the request.
    /// </summary>
    public static IApplicationBuilder UseProxyDetection(this IApplicationBuilder app, bool forwardedHeadersEnabled)
    {
        return app.Use(async (context, next) =>
        {
            if (Volatile.Read(ref _proxyWarningLogged) == 0
                && context.Request.Headers.ContainsKey("X-Forwarded-For")
                && context.Connection.RemoteIpAddress is { } remote
                && (IPAddress.IsLoopback(remote) || IsPrivate(remote))
                && Interlocked.Exchange(ref _proxyWarningLogged, 1) == 0)
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ForwardedHeaders");
                if (!forwardedHeadersEnabled)
                    logger.LogWarning(
                        "Requests arrive through a proxy at {ProxyAddress} with X-Forwarded-For, but ForwardedHeaders:Enabled is false. " +
                        "All clients share that address for rate limiting. Set ForwardedHeaders__Enabled=true and ForwardedHeaders__KnownProxies__0={ProxyAddress}.",
                        remote, remote);
                else
                    logger.LogInformation("Forwarded headers active; proxy hop seen at {ProxyAddress}.", remote);
            }
            await next();
        });
    }

    internal static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
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
