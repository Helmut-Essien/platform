using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Client.Services;

public static class ClientServiceRegistration
{
    /// <summary>
    /// Auth/session state is app-wide in WASM and must be shared with the HttpClient message handlers,
    /// which IHttpClientFactory resolves from its own DI scope. As scoped services, ApiErrorHandler cleared
    /// the token and notified a different JwtAuthenticationStateProvider than the UI used (stale auth UI
    /// after a 401) and failure snackbars never reached the shell. Singletons fix that (one user per tab).
    /// </summary>
    public static IServiceCollection AddPlatformClientServices(
        this IServiceCollection services, string? configuredApiBaseUrl, string hostBaseAddress)
    {
        services.AddSingleton<TokenStorage>();
        services.AddSingleton<JwtAuthenticationStateProvider>();
        services.AddSingleton<AuthenticationStateProvider>(sp =>
            sp.GetRequiredService<JwtAuthenticationStateProvider>());
        services.AddSingleton<ApiFailureNotifier>();
        services.AddTransient<JwtAuthorizationMessageHandler>();
        services.AddTransient<ApiErrorHandler>();
        services.AddTransient<NetworkFailureHandler>();

        var baseUrl = !string.IsNullOrWhiteSpace(configuredApiBaseUrl)
            ? configuredApiBaseUrl.TrimEnd('/') + "/"
            : hostBaseAddress;

        services.AddHttpClient<PlatformApiClient>(client => client.BaseAddress = new Uri(baseUrl))
            .AddHttpMessageHandler<JwtAuthorizationMessageHandler>()
            .AddHttpMessageHandler<ApiErrorHandler>()
            .AddHttpMessageHandler<NetworkFailureHandler>();

        // Anonymous pay page: no JWT, no login redirect on 401, no admin failure snackbars.
        services.AddHttpClient<PublicPayClient>(client => client.BaseAddress = new Uri(baseUrl));

        return services;
    }
}
