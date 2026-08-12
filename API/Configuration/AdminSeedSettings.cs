namespace Platform.Api.Configuration;

/// <summary>
/// Bootstrap Identity account settings. Layered by environment:
/// <list type="bullet">
/// <item><description>Development — demo admin (<c>appsettings.Development.json</c>)</description></item>
/// <item><description>Production — live admin via secrets/env only (<c>AdminSeed__Email</c>, <c>AdminSeed__Password</c>)</description></item>
/// </list>
/// Password must stay empty in committed non-Development config.
/// </summary>
public class AdminSeedSettings
{
    public const string SectionName = "AdminSeed";

    /// <summary>Login email for the bootstrap Admin user in this environment.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Password used only when creating the user if it does not exist.
    /// Never commit a production password; use environment variables or secret stores.
    /// </summary>
    public string Password { get; set; } = string.Empty;
}
