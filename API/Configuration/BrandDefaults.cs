namespace Platform.Api.Configuration;

/// <summary>
/// Fallback business identity used when the invoice brand profile (Settings) leaves a field empty, and to seed
/// a new profile. The saved brand profile always wins over these values.
/// </summary>
public static class BrandDefaults
{
    public const string CompanyName = "HelmutCode Solutions";
    public const string AddressLine1 = "YN13 Flask St";
    public const string AddressLine2 = "Accra, Ghana";
    public const string Phone = "+233500785153";
    public const string ContactEmail = "helmutessien@gmail.com";
    public const string Website = "helmutcode.runasp.net";

    /// <summary>The name the app used to seed before HelmutCode branding; treated as "not set".</summary>
    public const string LegacyPlaceholderName = "Platform License Hub";
}
