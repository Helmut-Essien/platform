namespace Platform.Api.Configuration;

public class JwtSettings
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "Platform.Api";

    public string Audience { get; set; } = "Platform.Client";

    public string Key { get; set; } = string.Empty;

    public int ExpiryMinutes { get; set; } = 480;
}
