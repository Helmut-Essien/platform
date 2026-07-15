using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;
using Platform.Client;
using Platform.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddAuthorizationCore();
builder.Services.AddMudServices();

builder.Services.AddScoped<TokenStorage>();
builder.Services.AddScoped<JwtAuthorizationMessageHandler>();
builder.Services.AddScoped<JwtAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
    sp.GetRequiredService<JwtAuthenticationStateProvider>());

builder.Services.AddScoped<ApiErrorHandler>();
builder.Services.AddScoped<PlatformApiClient>();
builder.Services.AddHttpClient<PlatformApiClient>(client =>
{
    var configBaseUrl = builder.Configuration["ApiBaseUrl"];
    var baseUrl = !string.IsNullOrEmpty(configBaseUrl)
        ? configBaseUrl.TrimEnd('/') + "/"
        : builder.HostEnvironment.BaseAddress;
    client.BaseAddress = new Uri(baseUrl);
})
.AddHttpMessageHandler<JwtAuthorizationMessageHandler>()
.AddHttpMessageHandler<ApiErrorHandler>();

await builder.Build().RunAsync();
