using System.Security.Claims;
using Platform.Client.Helpers;
using Xunit;

namespace Client.Tests;

public class UserDisplayTests
{
    private static ClaimsPrincipal User(params string[] roles) =>
        new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)), "jwt"));

    [Fact]
    public void Role_ComesFromClaims() => Assert.Equal("Admin", UserDisplay.Role(User("Admin")));

    [Fact]
    public void Role_MultipleRoles_AreJoinedAndDeduplicated() =>
        Assert.Equal("Admin, Billing", UserDisplay.Role(User("Admin", "Billing", "admin")));

    [Fact]
    public void Role_NoRoles_IsNeutral()
    {
        Assert.Equal("User", UserDisplay.Role(User()));
        Assert.Equal("User", UserDisplay.Role(null));
    }

    [Theory]
    [InlineData("helmut@example.com", "helmut")]
    [InlineData("", "Account")]
    [InlineData(null, "Account")]
    public void Name_UsesEmailLocalPart(string? email, string expected) =>
        Assert.Equal(expected, UserDisplay.Name(email));
}
