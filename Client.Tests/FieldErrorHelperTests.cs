using Platform.Client.Services;
using Xunit;

namespace Client.Tests;

public class FieldErrorHelperTests
{
    [Fact]
    public void GetFieldError_ReturnsFirstMatchingMessage()
    {
        var errors = new Dictionary<string, string[]>
        {
            ["CustomerId"] = ["Customer is required"],
            ["request.Currency"] = ["Currency is required"]
        };

        var message = FieldErrorHelper.GetFieldError(errors, "Currency", "request.Currency");

        Assert.Equal("Currency is required", message);
    }

    [Fact]
    public void GetFieldError_ReturnsNullWhenErrorsAreMissing()
    {
        var message = FieldErrorHelper.GetFieldError(null, "Email");

        Assert.Null(message);
    }

    [Fact]
    public void HasFieldErrors_OnlyReturnsTrueForNonEmptyErrors()
    {
        Assert.False(FieldErrorHelper.HasFieldErrors(null));
        Assert.False(FieldErrorHelper.HasFieldErrors(new Dictionary<string, string[]>()));
        Assert.True(FieldErrorHelper.HasFieldErrors(new Dictionary<string, string[]>
        {
            ["Email"] = ["Email is required"]
        }));
    }
}
