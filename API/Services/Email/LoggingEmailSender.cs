namespace Platform.Api.Services.Email;

/// <summary>Development fallback — logs recipient and subject only, never license key content.</summary>
public class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        var attachmentCount = attachments?.Count ?? 0;
        var attachmentNames = attachmentCount == 0
            ? "none"
            : string.Join(", ", attachments!.Select(a => a.FileName));

        logger.LogWarning(
            "Email provider is Logging. Would send to {Recipient} with subject \"{Subject}\" and {AttachmentCount} attachment(s): {AttachmentNames}. Configure Email:Provider=Resend or Smtp for real delivery.",
            toEmail,
            subject,
            attachmentCount,
            attachmentNames);

        return Task.CompletedTask;
    }
}
