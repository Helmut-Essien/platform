using Platform.Client.Helpers;
using Xunit;

namespace Client.Tests;

public class ReturnUrlTests
{
    [Theory]
    [InlineData("/customers", "/customers")]
    [InlineData("/licenses?filter=expiring", "/licenses?filter=expiring")]
    [InlineData("/invoices/01ABC", "/invoices/01ABC")]
    [InlineData("/audit?q=a:b", "/audit?q=a:b")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("\\\\evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/javascript:alert(1)", "/")]
    [InlineData("customers", "/")]
    [InlineData("/login", "/")]
    [InlineData("/LOGIN/", "/")]
    [InlineData("/login?returnUrl=%2Flogin", "/")]
    [InlineData("/forgot-password", "/")]
    [InlineData("/reset-password?token=x", "/")]
    [InlineData("/cust\nomers", "/")]
    public void Sanitize_AllowsOnlyLocalNonAuthPaths(string? input, string expected) =>
        Assert.Equal(expected, ReturnUrl.Sanitize(input));

    [Theory]
    [InlineData("", "login")]
    [InlineData("login", "login")]
    [InlineData("login?returnUrl=%2Fx", "login")]
    [InlineData("customers", "login?returnUrl=%2Fcustomers")]
    [InlineData("licenses?filter=expiring", "login?returnUrl=%2Flicenses%3Ffilter%3Dexpiring")]
    public void LoginUrlFor_EncodesCurrentLocation(string relative, string expected) =>
        Assert.Equal(expected, ReturnUrl.LoginUrlFor(relative));
}
