using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Platform.Client.Services;
using Xunit;

namespace Client.Tests;

public class JwtAuthenticationStateProviderTests
{
    [Fact]
    public void ExtractRoles_ReadsRoleClaimFromJwtPayload()
    {
        var jwt = CreateUnsignedJwt(new Dictionary<string, object>
        {
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            ["role"] = "Admin"
        });

        var roles = JwtAuthenticationStateProvider.ExtractRoles(jwt);

        Assert.Equal(["Admin"], roles);
    }

    [Fact]
    public void ExtractRoles_ReadsFullClaimTypeUri()
    {
        var jwt = CreateUnsignedJwt(new Dictionary<string, object>
        {
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            [ClaimTypes.Role] = "Admin"
        });

        var roles = JwtAuthenticationStateProvider.ExtractRoles(jwt);

        Assert.Equal(["Admin"], roles);
    }

    [Fact]
    public void ExtractRoles_ReturnsEmptyWhenNoRoleClaims()
    {
        var jwt = CreateUnsignedJwt(new Dictionary<string, object>
        {
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            ["email"] = "a@b.c"
        });

        Assert.Empty(JwtAuthenticationStateProvider.ExtractRoles(jwt));
    }

    private static string CreateUnsignedJwt(Dictionary<string, object> payload)
    {
        static string B64(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var header = B64(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}"""));
        var body = B64(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        return $"{header}.{body}.sig";
    }
}
