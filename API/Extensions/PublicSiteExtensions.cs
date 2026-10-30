namespace Platform.Api.Extensions;

/// <summary>
/// Public, search-indexable part of the site. "/" is the static, server-rendered landing page
/// (wwwroot/landing.html, no WASM); the admin SPA lives on every other path (/dashboard, /login,
/// /invoices/..., /pay/{token}) exactly as before, so links already sent in emails keep working.
/// Every response except the landing page and its public assets carries "X-Robots-Tag: noindex".
/// </summary>
public static class PublicSiteExtensions
{
    public const string LandingFile = "/landing.html";
    internal const string NoIndexValue = "noindex, nofollow";

    // Files that crawlers and link previews may fetch and index. Everything else is noindex.
    private static readonly HashSet<string> PublicAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        "/robots.txt", "/sitemap.xml", "/og-image.png", "/favicon.ico", "/favicon.svg", "/favicon.png",
        "/favicon-32.png", "/apple-touch-icon.png", "/icon-192.png", "/icon-512.png", "/site.webmanifest",
    };

    // Text assets compressed on the fly (small, no secrets, so no BREACH concern).
    private static readonly HashSet<string> CompressibleAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        LandingFile, "/js/landing.js", "/robots.txt", "/sitemap.xml", "/favicon.svg", "/site.webmanifest",
    };

    internal static bool IsLandingRequest(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && request.Path.Value is "/" or "";

    internal static bool IsIndexable(PathString path) =>
        path.Value is "/" or "" || PublicAssets.Contains(path.Value!);

    /// <summary>Must run before UseBlazorFrameworkFiles/UseStaticFiles.</summary>
    public static IApplicationBuilder UsePublicSite(this IApplicationBuilder app)
    {
        app.Use((context, next) =>
        {
            if (!IsIndexable(context.Request.Path))
            {
                var response = context.Response;
                response.OnStarting(() =>
                {
                    response.Headers["X-Robots-Tag"] = NoIndexValue;
                    return Task.CompletedTask;
                });
            }

            if (IsLandingRequest(context.Request))
            {
                context.Request.Path = LandingFile;
                // WebApplication routes before any user middleware, so "/" has already matched the SPA
                // fallback endpoint; clear it so UseStaticFiles serves landing.html instead.
                context.SetEndpoint(null);
            }

            return next(context);
        });

        app.UseWhen(
            context => HttpMethods.IsGet(context.Request.Method) && CompressibleAssets.Contains(context.Request.Path.Value ?? ""),
            branch => branch.UseResponseCompression());

        return app;
    }

    /// <summary>
    /// SPA fallbacks. The default one skips paths whose last segment looks like a file name, and pay-link
    /// tokens ("{invoiceId}.{mac}") contain a dot, so /pay/{token} needs its own fallback or it 404s.
    /// </summary>
    public static IEndpointRouteBuilder MapPublicSiteFallbacks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapFallbackToFile("/pay/{**token}", "index.html", StaticFileCaching.Options());
        endpoints.MapFallbackToFile("index.html", StaticFileCaching.Options());
        return endpoints;
    }
}
