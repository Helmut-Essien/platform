using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Api.Extensions;
using Xunit;

namespace API.Tests;

/// <summary>
/// "/" is the static landing page; every SPA path (admin, /login, /pay/{token}, email links) still gets
/// the SPA shell. Runs the same middleware order as Program.cs over the real Client/wwwroot files.
/// </summary>
public sealed class PublicSiteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    internal static string ClientWwwRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Client", "wwwroot");
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            WebRootPath = ClientWwwRoot(),
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddPlatformResponseCompression();
        builder.Logging.ClearProviders();

        _app = builder.Build();
        _app.UsePublicSite();
        _app.UseStaticFiles(StaticFileCaching.Options());
        _app.UsePlatformResponseCompression();
        _app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        _app.MapGet("/api/ping", () => Results.Ok(new { ok = true }));
        _app.MapFallback("/api/{**slug}", () => Results.NotFound());
        _app.MapPublicSiteFallbacks();
        await _app.StartAsync();

        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            BaseAddress = new Uri(address),
        };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private static string NoIndex(HttpResponseMessage r) => string.Join(",", r.Headers.TryGetValues("X-Robots-Tag", out var v) ? v : []);

    [Fact]
    public async Task Root_ServesIndexableLandingPage_NotTheSpa()
    {
        var res = await _http.GetAsync("/");
        var html = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<html lang=\"en-GH\">", html);
        Assert.DoesNotContain("_framework/blazor", html);
        Assert.Equal("", NoIndex(res));
        Assert.Equal(TimeSpan.FromMinutes(5), res.Headers.CacheControl?.MaxAge);
        Assert.NotNull(res.Headers.ETag);
    }

    [Fact]
    public async Task Root_IsCompressed_WhenClientAcceptsIt()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "br, gzip");
        var res = await _http.SendAsync(req);
        Assert.Contains(res.Content.Headers.ContentEncoding, e => e is "br" or "gzip");
    }

    [Fact]
    public async Task Head_Root_ServesLanding()
    {
        var res = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("", NoIndex(res));
    }

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/login")]
    [InlineData("/login?returnUrl=%2Finvoices")]
    [InlineData("/reset-password?token=abc&email=a%40b.c")]
    [InlineData("/invoices/01JABC?payment=return&reference=PLT-1")]
    [InlineData("/licenses")]
    // Real pay tokens are "{invoiceId}.{mac}": the dot made the default SPA fallback treat them as files (404).
    [InlineData("/pay/01JABC.c2lnbmF0dXJl")]
    [InlineData("/pay/01KABCDEF0123456789ABCDEFG.AbC-_dEf012")]
    [InlineData("/index.html")]
    public async Task SpaRoutes_StillServeTheAppShell_AndAreNoIndex(string path)
    {
        var res = await _http.GetAsync(path);
        var html = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("_framework/blazor.webassembly.js", html);
        Assert.Contains("<base href=\"/\"", html);
        Assert.Equal("no-cache", res.Headers.CacheControl?.ToString());
        Assert.Equal("noindex, nofollow", NoIndex(res));
    }

    [Fact]
    public async Task Api_And_Health_AreUnchanged_AndNoIndex()
    {
        var health = await _http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Contains("\"ok\"", await health.Content.ReadAsStringAsync());
        Assert.Equal("noindex, nofollow", NoIndex(health));

        var unknown = await _http.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("noindex, nofollow", NoIndex(await _http.GetAsync("/api/ping")));
    }

    [Theory]
    [InlineData("/robots.txt", "text/plain")]
    [InlineData("/sitemap.xml", "text/xml")]
    [InlineData("/site.webmanifest", "application/manifest+json")]
    [InlineData("/og-image.png", "image/png")]
    [InlineData("/favicon.ico", "image/x-icon")]
    [InlineData("/favicon.svg", "image/svg+xml")]
    [InlineData("/icon-512.png", "image/png")]
    [InlineData("/apple-touch-icon.png", "image/png")]
    public async Task PublicAssets_AreServed_AndIndexable(string path, string mediaType)
    {
        var res = await _http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(mediaType, res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("", NoIndex(res));
        Assert.NotNull(res.Headers.CacheControl?.MaxAge);
    }

    [Fact]
    public async Task Robots_AllowsOnlyRoot_AndPointsAtSitemap()
    {
        var robots = await _http.GetStringAsync("/robots.txt");
        Assert.Contains("Allow: /$", robots);
        Assert.Contains("Disallow: /", robots);
        Assert.Contains("Sitemap: https://helmutcode.runasp.net/sitemap.xml", robots);

        var sitemap = await _http.GetStringAsync("/sitemap.xml");
        Assert.Contains("<loc>https://helmutcode.runasp.net/</loc>", sitemap);
    }
}

/// <summary>Static checks on landing.html: SEO tags, structured data and accessibility basics.</summary>
public class LandingPageContentTests
{
    private static readonly string Html = File.ReadAllText(Path.Combine(PublicSiteTests.ClientWwwRoot(), "landing.html"));

    [Fact]
    public void HasExactlyOneH1_AndLandmarks()
    {
        Assert.Single(Regex.Matches(Html, "<h1[\\s>]"));
        Assert.Contains("<header", Html);
        Assert.Contains("<main id=\"main\"", Html);
        Assert.Contains("<footer", Html);
        Assert.Contains("href=\"#main\"", Html);
    }

    [Theory]
    [InlineData("<link rel=\"canonical\" href=\"https://helmutcode.runasp.net/\">")]
    [InlineData("<meta name=\"description\"")]
    [InlineData("<meta property=\"og:image\" content=\"https://helmutcode.runasp.net/og-image.png\">")]
    [InlineData("<meta name=\"twitter:card\" content=\"summary_large_image\">")]
    [InlineData("<link rel=\"manifest\" href=\"/site.webmanifest\">")]
    [InlineData("href=\"/login\" rel=\"nofollow\">Client login")]
    public void HasSeoTags(string fragment) => Assert.Contains(fragment, Html);

    [Fact]
    public void OnlyDeferredFirstPartyScript_NoExternalResources()
    {
        // JSON-LD plus one same-origin module script (modules are deferred, so never render-blocking).
        var scripts = Regex.Matches(Html, "<script(?![^>]*application/ld\\+json)[^>]*>").Select(m => m.Value).ToList();
        Assert.Equal(["<script type=\"module\" src=\"/js/landing.js\">"], scripts);
        Assert.DoesNotContain("<link rel=\"stylesheet\"", Html);
        Assert.DoesNotContain("fonts.googleapis", Html);
        Assert.DoesNotMatch("(src|href)=\"https?://(?!helmutcode\\.runasp\\.net)", Html);
    }

    [Fact]
    public void HeaderHasNoLoginCta_FooterHasLogin()
    {
        var header = Regex.Match(Html, "<header.*?</header>", RegexOptions.Singleline).Value;
        Assert.DoesNotContain("login", header, StringComparison.OrdinalIgnoreCase);
        var footer = Regex.Match(Html, "<footer.*?</footer>", RegexOptions.Singleline).Value;
        Assert.Contains("href=\"/login\"", footer);
    }

    [Fact]
    public void SceneCanvas_IsDecorative() =>
        Assert.Contains("<canvas id=\"scene\" aria-hidden=\"true\"", Html);

    [Fact]
    public void JsonLd_IsValid_AndFaqMatchesVisibleText()
    {
        var json = Regex.Match(Html, "<script type=\"application/ld\\+json\">(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;
        using var doc = JsonDocument.Parse(json);
        var graph = doc.RootElement.GetProperty("@graph").EnumerateArray().ToList();
        Assert.Contains(graph, n => n.GetProperty("@type").GetString() == "Organization");
        Assert.Contains(graph, n => n.GetProperty("@type").GetString() == "ProfessionalService");

        var faq = graph.Single(n => n.GetProperty("@type").GetString() == "FAQPage");
        var questions = faq.GetProperty("mainEntity").EnumerateArray().ToList();
        var visible = Regex.Matches(Html, "<details><summary>(.*?)</summary><p>(.*?)</p></details>");
        Assert.Equal(visible.Count, questions.Count);
        for (var i = 0; i < questions.Count; i++)
        {
            Assert.Equal(visible[i].Groups[1].Value, questions[i].GetProperty("name").GetString());
            Assert.Equal(visible[i].Groups[2].Value, questions[i].GetProperty("acceptedAnswer").GetProperty("text").GetString());
        }
    }
}
