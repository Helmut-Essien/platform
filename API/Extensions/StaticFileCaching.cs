using Microsoft.AspNetCore.StaticFiles;

namespace Platform.Api.Extensions;

/// <summary>
/// index.html is not fingerprinted: without no-cache a browser may keep a stale shell after a deploy
/// and boot old _framework assets (integrity failures). The landing page and crawler files get short
/// public caching (revalidated by ETag); icons and the social image a day. Other files keep the defaults.
/// </summary>
public static class StaticFileCaching
{
    public static StaticFileOptions Options()
    {
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".webmanifest"] = "application/manifest+json";

        return new StaticFileOptions
        {
            ContentTypeProvider = types,
            OnPrepareResponse = ctx =>
            {
                var cacheControl = CacheControlFor(ctx.File.Name);
                if (cacheControl is not null)
                    ctx.Context.Response.Headers.CacheControl = cacheControl;
            }
        };
    }

    internal static bool IsAppShell(string fileName) =>
        string.Equals(fileName, "index.html", StringComparison.OrdinalIgnoreCase);

    internal static string? CacheControlFor(string fileName) => fileName.ToLowerInvariant() switch
    {
        "index.html" => "no-cache",
        "landing.html" or "landing.js" => "public, max-age=300, must-revalidate",
        "robots.txt" or "sitemap.xml" or "site.webmanifest" => "public, max-age=3600",
        "og-image.png" or "favicon.ico" or "favicon.svg" or "favicon.png" or "favicon-32.png"
            or "apple-touch-icon.png" or "icon-192.png" or "icon-512.png" => "public, max-age=86400",
        _ => null,
    };
}
