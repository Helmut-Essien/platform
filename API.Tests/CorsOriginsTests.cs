using Microsoft.Extensions.Configuration;
using Platform.Api.Extensions;
using Xunit;

namespace API.Tests;

public class CorsOriginsTests
{
    [Fact]
    public void Resolve_MergesConfigurationAndEnvironment()
    {
        Environment.SetEnvironmentVariable("CORS_ORIGINS", "https://admin.example.com, https://staging.example.com");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Cors:Origins:0"] = "https://configured.example.com"
                })
                .Build();

            var origins = CorsOrigins.Resolve(configuration);

            Assert.Equal(3, origins.Length);
            Assert.Contains("https://configured.example.com", origins);
            Assert.Contains("https://admin.example.com", origins);
            Assert.Contains("https://staging.example.com", origins);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CORS_ORIGINS", null);
        }
    }
}
