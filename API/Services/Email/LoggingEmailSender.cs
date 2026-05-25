namespace Platform.Api.Services.Email;

/// <summary>Development fallback — logs recipient and subject only, never license key content.</summary>
public class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        logger.LogWarning(
            "Email provider is Logging. Would send to {Recipient} with subject \"{Subject}\". Configure Email:Provider=Smtp for real delivery.",
            toEmail,
            subject);

        return Task.CompletedTask;
    }
}
