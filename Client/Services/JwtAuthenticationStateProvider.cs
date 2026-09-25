using System.Text.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Platform.Client.Services;

public class JwtAuthenticationStateProvider(TokenStorage tokenStorage) : AuthenticationStateProvider
{
    private static readonly string[] RoleClaimTypes =
    [
        ClaimTypes.Role,
        "role",
        "roles"
    ];

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var token = await tokenStorage.GetTokenAsync();
        if (string.IsNullOrWhiteSpace(token))
            return Anonymous();

        if (!TryParsePayload(token, out var payload) || IsExpired(payload))
        {
            await tokenStorage.ClearAsync();
            return Anonymous();
        }

        var email = await tokenStorage.GetEmailAsync() ?? "";
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, email),
            new(ClaimTypes.Email, email)
        };

        foreach (var role in ExtractRoles(payload))
            claims.Add(new Claim(ClaimTypes.Role, role));

        var identity = new ClaimsIdentity(claims, authenticationType: "jwt");
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    public void NotifyStateChanged() =>
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    private static AuthenticationState Anonymous() =>
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    public static IReadOnlyList<string> ExtractRoles(string jwt) =>
        TryParsePayload(jwt, out var payload) ? ExtractRoles(payload) : [];

    public static IReadOnlyList<string> ExtractRoles(JsonElement payload)
    {
        var roles = new List<string>();
        foreach (var claimType in RoleClaimTypes)
        {
            if (!payload.TryGetProperty(claimType, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                roles.Add(value.GetString()!);
            else if (value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                        roles.Add(item.GetString()!);
                }
            }
        }

        return roles;
    }

    private static bool IsExpired(JsonElement payload)
    {
        if (payload.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var unix))
            return DateTimeOffset.FromUnixTimeSeconds(unix) < DateTimeOffset.UtcNow;
        return true;
    }

    private static bool TryParsePayload(string jwt, out JsonElement payload)
    {
        payload = default;
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2)
                return false;

            var padded = parts[1].PadRight(parts[1].Length + (4 - parts[1].Length % 4) % 4, '=');
            var json = Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'));
            payload = JsonSerializer.Deserialize<JsonElement>(json);
            return payload.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }
}
