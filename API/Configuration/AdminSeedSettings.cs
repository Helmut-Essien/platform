namespace Platform.Api.Configuration;

public class AdminSeedSettings
{
    public const string SectionName = "AdminSeed";

    public string Email { get; set; } = "admin@platform.local";

    public string Password { get; set; } = string.Empty;
}
