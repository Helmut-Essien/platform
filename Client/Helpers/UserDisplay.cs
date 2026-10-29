using System.Security.Claims;

namespace Platform.Client.Helpers;

/// <summary>Shell labels derived from the signed-in user's claims.</summary>
public static class UserDisplay
{
    public const string NoRoleLabel = "User";

    /// <summary>The user's role(s) from their claims, e.g. "Admin" or "Admin, Billing"; "User" when none.</summary>
    public static string Role(ClaimsPrincipal? user)
    {
        var roles = user?.FindAll(ClaimTypes.Role)
            .Select(c => c.Value.Trim())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        return roles.Count == 0 ? NoRoleLabel : string.Join(", ", roles);
    }

    /// <summary>Local part of the email, or "Account" when unknown.</summary>
    public static string Name(string? email) =>
        string.IsNullOrWhiteSpace(email) ? "Account" : email.Split('@')[0];
}
