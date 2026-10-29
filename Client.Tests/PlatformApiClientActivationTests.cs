using Microsoft.Extensions.DependencyInjection;
using Platform.Client.Services;
using Xunit;

namespace Client.Tests;

public class PlatformApiClientActivationTests
{
    // Regression: a second public constructor made the typed-client factory throw MultipleCtorsFound,
    // so every page that injected PlatformApiClient (including /login) failed to render.
    [Fact]
    public void TypedHttpClient_CanBeResolvedFromDi()
    {
        var services = new ServiceCollection();
        services.AddScoped<ApiFailureNotifier>();
        services.AddHttpClient<PlatformApiClient>(c => c.BaseAddress = new Uri("http://localhost/"));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<PlatformApiClient>());
    }
}
