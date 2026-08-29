namespace Platform.Api.Configuration;

public class AuthSettings
{
    public const string SectionName = "Auth";

    /// <summary>Blazor admin UI base URL used in password-reset links.</summary>
    public string ClientBaseUrl { get; set; } = "http://localhost:5154";
}
