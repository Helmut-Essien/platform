using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Platform.Client.Services;
using Xunit;

namespace Client.Tests;

public class PublicPayClientTests
{
    private sealed class Stub(HttpStatusCode status, string body = "{}") : HttpMessageHandler
    {
        public HttpRequestMessage? Last;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, PublicPayClient.Outcome.Invalid)]
    [InlineData(HttpStatusCode.Gone, PublicPayClient.Outcome.Expired)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PublicPayClient.Outcome.Unavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, PublicPayClient.Outcome.RateLimited)]
    [InlineData(HttpStatusCode.Unauthorized, PublicPayClient.Outcome.Failed)]
    public async Task MapsStatusCodes(HttpStatusCode status, PublicPayClient.Outcome expected)
    {
        var stub = new Stub(status, """{"detail":"x"}""");
        var client = new PublicPayClient(new HttpClient(stub) { BaseAddress = new Uri("https://h.test/") });
        var result = await client.GetSummaryAsync("TOKEN.abc");
        Assert.Equal(expected, result.Outcome);
        Assert.Null(stub.Last!.Headers.Authorization);
        Assert.Equal("/api/public/pay/TOKEN.abc", stub.Last.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task NetworkFailure_IsReportedNotThrown()
    {
        var client = new PublicPayClient(new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("https://h.test/") });
        Assert.Equal(PublicPayClient.Outcome.Failed, (await client.GetStatusAsync("t", "r")).Outcome);
    }

    [Fact]
    public void PayPage_IsAnonymous()
    {
        var page = typeof(Platform.Client.Pages.Pay);
        Assert.NotNull(page.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Contains(page.GetCustomAttributes<Microsoft.AspNetCore.Components.RouteAttribute>(), r => r.Template == "/pay/{Token}");
    }

    [Fact]
    public void Registered_WithoutAdminHandlers()
    {
        var services = new ServiceCollection();
        services.AddPlatformClientServices(null, "https://h.test/");
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<PublicPayClient>());
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("down");
    }
}
