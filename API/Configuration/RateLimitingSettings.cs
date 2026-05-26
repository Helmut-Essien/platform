namespace Platform.Api.Configuration;

public class RateLimitingSettings
{
    public const string SectionName = "RateLimiting";

    public int LicenseValidatePermitLimit { get; set; } = 60;

    public int LicenseValidateWindowMinutes { get; set; } = 1;
}
