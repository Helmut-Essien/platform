using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Services.Email;
using Xunit;

namespace API.Tests;

public class ResendEmailSenderTests
{
    [Fact]
    public async Task SendAsync_PostsExpectedPayloadAndSucceeds()
    {
        HttpRequestMessage? captured = null;
        string? requestBody = null;

        var handler = new StubHandler(async request =>
        {
            captured = request;
            requestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"email_123"}""", Encoding.UTF8, "application/json")
            };
        });

        var sender = CreateSender(handler, new EmailSettings
        {
            Provider = "Resend",
            FromAddress = "noreply@example.com",
            FromName = "Platform License Hub",
            ResendApiKey = "re_test_key"
        });

        await sender.SendAsync("customer@example.com", "Your license is active", "<p>key</p>");

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("https://api.resend.com/emails", captured.RequestUri!.ToString());
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("re_test_key", captured.Headers.Authorization.Parameter);
        Assert.Contains("Platform License Hub", requestBody);
        Assert.Contains("noreply@example.com", requestBody);
        Assert.Contains("\"to\":[\"customer@example.com\"]", requestBody);
        Assert.Contains("\"subject\":\"Your license is active\"", requestBody);
        Assert.Contains("\"html\":\"\\u003Cp\\u003Ekey\\u003C/p\\u003E\"", requestBody);
    }

    [Fact]
    public async Task SendAsync_ThrowsWithResponseBodyWhenApiFails()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"message":"API key is invalid"}""", Encoding.UTF8, "application/json")
        }));

        var sender = CreateSender(handler, new EmailSettings
        {
            FromAddress = "noreply@example.com",
            ResendApiKey = "re_bad"
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sender.SendAsync("customer@example.com", "Subject", "<p>body</p>"));

        Assert.Contains("401", ex.Message);
        Assert.Contains("API key is invalid", ex.Message);
    }

    [Fact]
    public async Task SendAsync_RequiresApiKey()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var sender = CreateSender(handler, new EmailSettings
        {
            FromAddress = "noreply@example.com",
            ResendApiKey = ""
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sender.SendAsync("customer@example.com", "Subject", "<p>body</p>"));

        Assert.Contains("ResendApiKey", ex.Message);
    }

    private static ResendEmailSender CreateSender(HttpMessageHandler handler, EmailSettings settings)
    {
        var factory = new StubHttpClientFactory(handler);
        return new ResendEmailSender(
            factory,
            Options.Create(settings),
            NullLogger<ResendEmailSender>.Instance);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("https://api.resend.com/")
            };
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            responder(request);
    }
}
