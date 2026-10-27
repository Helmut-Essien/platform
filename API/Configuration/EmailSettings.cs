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

    public EmailOutboxSettings Outbox { get; set; } = new();
}

public class EmailOutboxSettings
{
    public int PollIntervalSeconds { get; set; } = 10;
    public int BatchSize { get; set; } = 20;
    public int MaxAttempts { get; set; } = 10;

    /// <summary>Dead-lettered messages keep their encrypted payload (so they can be retried) this many days.</summary>
    public int DeadLetterPayloadRetentionDays { get; set; } = 14;

    /// <summary>Sent and cancelled outbox rows older than this many days (minimum 7) are deleted.</summary>
    public int SentRetentionDays { get; set; } = 90;
    public string? EncryptionKey { get; set; }
}
