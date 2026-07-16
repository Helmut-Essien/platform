namespace Platform.Api.Configuration;

public class EmailSettings
{
    public const string SectionName = "Email";

    public string Provider { get; set; } = "Logging";

    public string FromAddress { get; set; } = "noreply@platform.local";

    public string FromName { get; set; } = "Platform License Hub";

    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public bool EnableSsl { get; set; } = true;

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>Max seconds to wait for SMTP/HTTP email send before failing. Default 30.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    public string? ResendApiKey { get; set; }

    public string? SendGridApiKey { get; set; }
}
