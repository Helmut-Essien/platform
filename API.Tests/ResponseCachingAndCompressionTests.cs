using Microsoft.AspNetCore.Http;
using Platform.Api.Extensions;
using Xunit;

namespace API.Tests;

public class ResponseCachingAndCompressionTests
{
    [Theory]
    [InlineData("GET", "/api/invoices", true)]
    [InlineData("GET", "/api/audit-logs/paged", true)]
    [InlineData("GET", "/api/auth/me", false)]
    [InlineData("POST", "/api/integration-keys", false)]
    [InlineData("GET", "/_framework/blazor.webassembly.js", false)]
    [InlineData("GET", "/apiary", false)]
    public void ShouldCompress_OnlyApiGetsOutsideAuth(string method, string path, bool expected)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;

        Assert.Equal(expected, ResponseCompressionExtensions.ShouldCompress(ctx));
    }

    [Theory]
    [InlineData("index.html", true)]
    [InlineData("INDEX.HTML", true)]
    [InlineData("app.css", false)]
    public void IsAppShell_OnlyIndexHtml(string name, bool expected) =>
        Assert.Equal(expected, StaticFileCaching.IsAppShell(name));
}
