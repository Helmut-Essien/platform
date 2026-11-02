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
    [InlineData("/", "/dashboard")]
    [InlineData("/dashboard", "/dashboard")]
    [InlineData(null, "/dashboard")]
    [InlineData("", "/dashboard")]
    [InlineData("https://evil.example", "/dashboard")]
    [InlineData("//evil.example", "/dashboard")]
    [InlineData("/\\evil.example", "/dashboard")]
    [InlineData("\\\\evil.example", "/dashboard")]
    [InlineData("javascript:alert(1)", "/dashboard")]
    [InlineData("/javascript:alert(1)", "/dashboard")]
    [InlineData("customers", "/dashboard")]
    [InlineData("/login", "/dashboard")]
    [InlineData("/LOGIN/", "/dashboard")]
    [InlineData("/login?returnUrl=%2Flogin", "/dashboard")]
    [InlineData("/forgot-password", "/dashboard")]
    [InlineData("/reset-password?token=x", "/dashboard")]
    [InlineData("/cust\nomers", "/dashboard")]
    [InlineData("/invoices/INV1?payment=return&reference=PLT-1", "/invoices/INV1?payment=return&reference=PLT-1")]
    public void Sanitize_AllowsOnlyLocalNonAuthPaths(string? input, string expected) =>
        Assert.Equal(expected, ReturnUrl.Sanitize(input));

    [Theory]
    [InlineData("", "login")]
    [InlineData("dashboard", "login")]
    [InlineData("login", "login")]
    [InlineData("login?returnUrl=%2Fx", "login")]
    [InlineData("customers", "login?returnUrl=%2Fcustomers")]
    [InlineData("licenses?filter=expiring", "login?returnUrl=%2Flicenses%3Ffilter%3Dexpiring")]
    // Session expired while paying at Paystack: the return query must survive login.
    [InlineData("invoices/INV1?payment=return&trxref=PLT-1&reference=PLT-1",
        "login?returnUrl=%2Finvoices%2FINV1%3Fpayment%3Dreturn%26trxref%3DPLT-1%26reference%3DPLT-1")]
    public void LoginUrlFor_EncodesCurrentLocation(string relative, string expected) =>
        Assert.Equal(expected, ReturnUrl.LoginUrlFor(relative));
}
