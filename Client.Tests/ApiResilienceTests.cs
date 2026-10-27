using System.Globalization;
using System.Net;
using Platform.Client.Services;
using Xunit;

namespace Client.Tests;

public class ApiResilienceTests
{
    private sealed class Throwing(Exception ex) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            throw ex;
        }
    }

    private sealed class Respond(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public Uri? LastUri;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(f(request));
        }
    }

    private static (PlatformApiClient Client, ApiFailureNotifier Notifier, List<string> Messages) Build(
        HttpMessageHandler inner, bool withNetworkHandler)
    {
        HttpMessageHandler pipeline = inner;
        if (withNetworkHandler)
            pipeline = new NetworkFailureHandler { InnerHandler = inner };

        var notifier = new ApiFailureNotifier();
        var messages = new List<string>();
        notifier.Failed += messages.Add;
        var http = new HttpClient(pipeline) { BaseAddress = new Uri("https://platform.test/") };
        return (new PlatformApiClient(http, notifier), notifier, messages);
    }

    [Fact]
    public async Task NetworkFailureHandler_ConvertsTransportErrorTo503Json()
    {
        var (client, _, messages) = Build(new Throwing(new HttpRequestException("offline")), true);

        var page = await client.GetCustomersPagedAsync(page: 2, pageSize: 10);

        Assert.Empty(page.Items);
        Assert.Equal(2, page.Page);
        Assert.Single(messages);
        Assert.Equal(NetworkFailureHandler.UnreachableMessage, messages[0]);
    }

    [Fact]
    public async Task NetworkFailureHandler_TreatsTimeoutAsUnreachable_ButNotCallerCancellation()
    {
        var (client, _, messages) = Build(new Throwing(new TaskCanceledException("timeout")), true);
        var result = await client.GetInvoiceStatsAsync();
        Assert.Null(result);
        Assert.Single(messages);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (client2, _, _) = Build(new Throwing(new OperationCanceledException(cts.Token)), true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client2.GetInvoiceStatsAsync(cts.Token));
    }

    [Fact]
    public async Task GetLists_OnServerError_ReturnEmptyAndNotify()
    {
        var (client, _, messages) = Build(
            new Respond(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)), false);

        Assert.Empty(await client.GetIntegrationKeysAsync());
        Assert.Empty(await client.GetServiceProductsAsync());
        Assert.Null(await client.GetDashboardStatsAsync());
        Assert.Equal(3, messages.Count);
    }

    [Fact]
    public async Task GetLists_OnUnauthorized_DoNotAddSnackbarNoise()
    {
        var (client, _, messages) = Build(
            new Respond(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), false);

        Assert.Null(await client.GetDashboardStatsAsync());
        Assert.Empty(messages);
    }

    [Fact]
    public async Task GetLists_OnMalformedJson_ReturnEmptyAndNotify()
    {
        var (client, _, messages) = Build(new Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>proxy error</html>", System.Text.Encoding.UTF8, "application/json")
        }), false);

        Assert.Empty(await client.GetServiceProductsAsync());
        Assert.Single(messages);
    }

    [Fact]
    public async Task MutatingCall_OnNetworkFailure_ReturnsFailedResultInsteadOfThrowing()
    {
        var (client, _, _) = Build(new Throwing(new HttpRequestException("offline")), true);

        var result = await client.VoidInvoiceAsync("inv-1");

        Assert.False(result.IsSuccess);
        Assert.Equal(NetworkFailureHandler.UnreachableMessage, result.ErrorMessage);
    }

    [Fact]
    public async Task SuccessResponseWithGarbageBody_ReturnsFailedResult()
    {
        var (client, _, _) = Build(new Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json", System.Text.Encoding.UTF8, "application/json")
        }), false);

        var result = await client.VoidInvoiceAsync("inv-1");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task InvoiceDateFilters_UseInvariantCultureFormatting()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA"); // non-Gregorian calendar and digits
            var handler = new Respond(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"items\":[],\"totalCount\":0,\"page\":1,\"pageSize\":25}",
                    System.Text.Encoding.UTF8, "application/json")
            });
            var (client, _, _) = Build(handler, false);

            await client.GetInvoicesPagedAsync(
                issuedFrom: new DateTime(2026, 3, 1), issuedTo: new DateTime(2026, 3, 31));

            Assert.Contains("issuedFrom=2026-03-01", handler.LastUri!.Query);
            Assert.Contains("issuedTo=2026-03-31", handler.LastUri.Query);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
