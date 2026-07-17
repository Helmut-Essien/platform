using Microsoft.Extensions.Configuration;
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

    private static EmailPayloadProtector CreateProtector(byte fill)
    {
        var key = Enumerable.Repeat(fill, 32).ToArray();
        var settings = Options.Create(new EmailSettings
        {
            Outbox = new EmailOutboxSettings { EncryptionKey = Convert.ToBase64String(key) }
        });
        return new EmailPayloadProtector(settings, new ConfigurationBuilder().Build());
    }
}
