using Microsoft.AspNetCore.StaticFiles;

namespace Platform.Api.Extensions;

/// <summary>
/// index.html is not fingerprinted: without no-cache a browser may keep a stale shell after a deploy
/// and boot old _framework assets (integrity failures). Other static files keep the default ETag caching.
/// </summary>
public static class StaticFileCaching
{
    public static StaticFileOptions Options() => new()
    {
        OnPrepareResponse = ctx =>
        {
            if (IsAppShell(ctx.File.Name))
                ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    };

    internal static bool IsAppShell(string fileName) =>
        string.Equals(fileName, "index.html", StringComparison.OrdinalIgnoreCase);
}
