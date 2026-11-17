namespace Platform.Api.Configuration;

public class EmailSettings
{
    public const string SectionName = "Email";

    public string Provider { get; set; } = "Logging";

    public string FromAddress { get; set; } = "noreply@platform.local";

    public string FromName { get; set; } = BrandDefaults.CompanyName;

    /// <summary>
    /// Where customer replies go (Reply-To header). Defaults to the business contact email so "reply to this
    /// message" reaches a person even when FromAddress is a no-reply sender. Set to an empty string to omit it.
    /// </summary>
    public string? ReplyToAddress { get; set; } = BrandDefaults.ContactEmail;

    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public bool EnableSsl { get; set; } = true;

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>
    /// Max seconds to wait for one SMTP/HTTP email send before failing it (it is retried). Default 15: a healthy
    /// Gmail or Resend send takes 1–3 s, and emails go out one at a time, so a stuck send shouldn't hold up the rest.
    /// </summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    public const int DefaultTimeoutSeconds = 15;

    /// <summary>The send timeout in use (non-positive values fall back to the default).</summary>
    public int EffectiveTimeoutSeconds => TimeoutSeconds > 0 ? TimeoutSeconds : DefaultTimeoutSeconds;

    public string? ResendApiKey { get; set; }

    public string? SendGridApiKey { get; set; }

    public EmailOutboxSettings Outbox { get; set; } = new();
}

public class EmailOutboxSettings
{
    public int PollIntervalSeconds { get; set; } = 10;
    public int BatchSize { get; set; } = 20;
    public int MaxAttempts { get; set; } = 10;

    /// <summary>
    /// Wait before the first retry of a failed send (5–3600 s); each later retry waits twice as long, capped at 6 hours.
    /// Default 30: 30 s, 1 min, 2 min, 4 min, …
    /// </summary>
    public int RetryBaseSeconds { get; set; } = 30;

    /// <summary>
    /// Right after the app starts, an email left "Sending" by a process that stopped mid-send is picked up again once
    /// it has been untouched this long (30–600 s, and at least 4× the send timeout) instead of the normal 10 minutes.
    /// Default 120.
    /// </summary>
    public int StartupReclaimSeconds { get; set; } = 120;

    /// <summary>Dead-lettered messages keep their encrypted payload (so they can be retried) this many days.</summary>
    public int DeadLetterPayloadRetentionDays { get; set; } = 14;

    /// <summary>Sent and cancelled outbox rows older than this many days (minimum 7) are deleted.</summary>
    public int SentRetentionDays { get; set; } = 90;
    public string? EncryptionKey { get; set; }
}
