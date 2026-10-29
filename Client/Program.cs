using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor;
using MudBlazor.Services;
using Platform.Client;
using Platform.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddAuthorizationCore();
builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    config.SnackbarConfiguration.PreventDuplicates = true;
    config.SnackbarConfiguration.NewestOnTop = true;
    config.SnackbarConfiguration.ShowCloseIcon = true;
    config.SnackbarConfiguration.VisibleStateDuration = 4000;
    config.SnackbarConfiguration.HideTransitionDuration = 200;
    config.SnackbarConfiguration.ShowTransitionDuration = 200;
});

// Auth/session state is app-wide in WASM and must be shared with the HttpClient message handlers,
// which IHttpClientFactory resolves from its own DI scope. As scoped services, ApiErrorHandler cleared
// the token and notified a *different* JwtAuthenticationStateProvider than the UI uses (stale auth UI
// after a 401), and failure snackbars never reached the shell. Singletons fix that (one user per tab).
builder.Services.AddPlatformClientServices(builder.Configuration["ApiBaseUrl"], builder.HostEnvironment.BaseAddress);

await builder.Build().RunAsync();
