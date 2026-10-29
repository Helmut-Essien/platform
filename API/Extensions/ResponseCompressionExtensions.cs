using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;

namespace Platform.Api.Extensions;

/// <summary>
/// Compresses API JSON (list pages are the bulk of the traffic). Limited to GET /api/* outside
/// /api/auth: GET responses never carry secrets (keys and tokens are only returned by POSTs), so
/// HTTPS compression does not open a BREACH-style oracle. Blazor's _framework files are served
/// pre-compressed (.br/.gz) by UseBlazorFrameworkFiles and are not touched here.
/// </summary>
public static class ResponseCompressionExtensions
{
    public static IServiceCollection AddPlatformResponseCompression(this IServiceCollection services)
    {
        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ["application/json", "application/problem+json"];
        });
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        return services;
    }

    public static IApplicationBuilder UsePlatformResponseCompression(this IApplicationBuilder app) =>
        app.UseWhen(ShouldCompress, branch => branch.UseResponseCompression());

    internal static bool ShouldCompress(HttpContext context) =>
        HttpMethods.IsGet(context.Request.Method)
        && context.Request.Path.StartsWithSegments("/api")
        && !context.Request.Path.StartsWithSegments("/api/auth");
}
