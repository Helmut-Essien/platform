using Platform.Api.Extensions;
using Platform.Api.Services;
using Xunit;

namespace API.Tests;

public class DeploymentHardeningTests
{
    [Fact]
    public void GenerateLicenseKey_HasFourRandomSegments()
    {
        var key = LicenseKeyDeliveryService.GenerateLicenseKey("hostel");
        Assert.Matches(@"^HOSTEL(-[A-HJ-NP-Z2-9]{4}){4}$", key);
        Assert.NotEqual(key, LicenseKeyDeliveryService.GenerateLicenseKey("hostel"));
    }

    [Theory]
    [InlineData("Platform_Dev_Jwt_Signing_Key_Change_In_Production_Min32Chars")]
    [InlineData("change-me-to-at-least-32-random-characters-long")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("short")]
    public void IsWeakJwtKey_RejectsPlaceholders(string key) =>
        Assert.True(AuthenticationExtensions.IsWeakJwtKey(key));

    [Fact]
    public void IsWeakJwtKey_AcceptsRandomKey() =>
        Assert.False(AuthenticationExtensions.IsWeakJwtKey("q8V2rXw0LmT5zKp3Hc9Nf7Yb1Dj4Gs6Ua0Ee2Wi8"));

    [Fact]
    public void ForwardedHeaders_ParsesProxiesAndNetworks()
    {
        Assert.Single(ForwardedHeadersSetup.ParseProxies(["10.0.0.5", " "]));
        var net = Assert.Single(ForwardedHeadersSetup.ParseNetworks(["10.0.0.0/8"]));
        Assert.Equal(8, net.PrefixLength);
        Assert.Throws<InvalidOperationException>(() => ForwardedHeadersSetup.ParseNetworks(["10.0.0.0"]));
        Assert.Throws<InvalidOperationException>(() => ForwardedHeadersSetup.ParseProxies(["not-an-ip"]));
    }
}
