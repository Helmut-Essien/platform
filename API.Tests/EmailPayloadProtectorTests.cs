using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Services.Email;
using Xunit;

namespace API.Tests;

public class EmailPayloadProtectorTests
{
    [Fact]
    public void Protect_RoundTripsWithoutPlaintextAtRest()
    {
        var protector = CreateProtector(1);
        const string key = "HOSTEL-ABCD-2345";

        var encrypted = protector.Protect(key);

        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(key), encrypted);
        Assert.DoesNotContain(key, Convert.ToBase64String(encrypted));
        Assert.Equal(key, protector.Unprotect(encrypted));
    }

    [Fact]
    public void Unprotect_WithDifferentKeyFails()
    {
        var encrypted = CreateProtector(1).Protect("SCHOOL-ABCD-2345");

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => CreateProtector(2).Unprotect(encrypted));
    }

    [Fact]
    public void Constructor_InDevelopment_FallsBackToJwtKey()
    {
        var settings = Options.Create(new EmailSettings { Outbox = new EmailOutboxSettings() });
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "Platform_Dev_Jwt_Signing_Key_Change_In_Production_Min32Chars"
            })
            .Build();

        var protector = new EmailPayloadProtector(
            settings,
            config,
            new FakeHostEnvironment { EnvironmentName = Environments.Development });

        Assert.Equal("ok", protector.Unprotect(protector.Protect("ok")));
    }

    [Fact]
    public void Constructor_InProduction_RequiresOutboxEncryptionKey()
    {
        var settings = Options.Create(new EmailSettings { Outbox = new EmailOutboxSettings() });
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "Platform_Dev_Jwt_Signing_Key_Change_In_Production_Min32Chars"
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new EmailPayloadProtector(
                settings,
                config,
                new FakeHostEnvironment { EnvironmentName = Environments.Production }));

        Assert.Contains("Email:Outbox:EncryptionKey", ex.Message);
    }

    private static EmailPayloadProtector CreateProtector(byte fill)
    {
        var key = Enumerable.Repeat(fill, 32).ToArray();
        var settings = Options.Create(new EmailSettings
        {
            Outbox = new EmailOutboxSettings { EncryptionKey = Convert.ToBase64String(key) }
        });
        return new EmailPayloadProtector(
            settings,
            new ConfigurationBuilder().Build(),
            new FakeHostEnvironment { EnvironmentName = Environments.Development });
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "API.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
