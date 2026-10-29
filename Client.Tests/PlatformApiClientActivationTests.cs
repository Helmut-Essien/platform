using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.JSInterop;
using Platform.Client.Services;
using Xunit;

namespace Client.Tests;

public class PlatformApiClientActivationTests
{
    // Regression: a second public constructor made the typed-client factory throw MultipleCtorsFound,
    // so every page that injected PlatformApiClient (including /login) failed to render.
    [Fact]
    public void TypedHttpClient_CanBeResolvedFromAppRegistrations()
    {
        using var provider = BuildProvider(out _, out _);
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PlatformApiClient>());
    }

    // Regression: handlers live in the HttpClientFactory's own scope; a 401 must clear the token and
    // notify the SAME auth state provider the UI uses, and navigate to /login.
    [Fact]
    public async Task Unauthorized_ClearsTokenAndNotifiesTheUiAuthProvider()
    {
        using var provider = BuildProvider(out var js, out var nav);
        js.Store[TokenStorage.TokenKey] = "x.y.z";

        var uiProvider = provider.GetRequiredService<AuthenticationStateProvider>();
        var notified = false;
        uiProvider.AuthenticationStateChanged += _ => notified = true;

        using var scope = provider.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<PlatformApiClient>();
        await api.GetMeAsync();

        Assert.True(notified);
        Assert.False(js.Store.ContainsKey(TokenStorage.TokenKey));
        Assert.EndsWith("/login", nav.Uri);
    }

    private static ServiceProvider BuildProvider(out FakeJs js, out TestNav nav)
    {
        var services = new ServiceCollection();
        js = new FakeJs();
        nav = new TestNav();
        services.AddSingleton<IJSRuntime>(js);
        services.AddSingleton<NavigationManager>(nav);
        services.AddPlatformClientServices(null, "http://localhost/");
        services.AddHttpClient<PlatformApiClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new StatusHandler(HttpStatusCode.Unauthorized));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private sealed class StatusHandler(HttpStatusCode code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code) { RequestMessage = request });
    }

    private sealed class TestNav : NavigationManager
    {
        public TestNav() => Initialize("http://localhost/", "http://localhost/customers");
        protected override void NavigateToCore(string uri, NavigationOptions options) =>
            Uri = ToAbsoluteUri(uri).ToString();
    }

    private sealed class FakeJs : IJSRuntime
    {
        public Dictionary<string, string> Store { get; } = new();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args)
        {
            var key = (string)args![0]!;
            switch (identifier)
            {
                case "sessionStorage.setItem": Store[key] = (string)args[1]!; break;
                case "sessionStorage.removeItem": Store.Remove(key); break;
                case "sessionStorage.getItem":
                    return new ValueTask<TValue>((TValue)(object?)(Store.TryGetValue(key, out var v) ? v : null)!);
            }
            return new ValueTask<TValue>(default(TValue)!);
        }
    }
}
