using System.Net;
using Microsoft.AspNetCore.Components;

namespace Platform.Client.Services;

public class ApiErrorHandler(
    TokenStorage tokenStorage,
    JwtAuthenticationStateProvider authState,
    NavigationManager navigation) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized
            && request.RequestUri?.AbsolutePath.Contains("/api/auth/login", StringComparison.OrdinalIgnoreCase) != true)
        {
            await tokenStorage.ClearAsync();
            authState.NotifyStateChanged();
            navigation.NavigateTo("/login");
        }

        return response;
    }
}
