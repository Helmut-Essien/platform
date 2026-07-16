using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;

namespace Platform.Api.Services.Email;

public class SmtpEmailSender(IOptions<EmailSettings> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        IReadOnlyList<EmailAttachment>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        if (string.IsNullOrWhiteSpace(settings.Host))
            throw new InvalidOperationException("Email:Host is required for SMTP provider.");

        var timeoutSeconds = settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 30;
        var hasCredentials = !string.IsNullOrWhiteSpace(settings.Username);
        var attachmentCount = attachments?.Count ?? 0;
        logger.LogInformation(
            "Sending SMTP email to {Recipient} via {SmtpHost}:{SmtpPort} (Ssl={EnableSsl}, Auth={HasCredentials}, From={FromAddress}, TimeoutSeconds={TimeoutSeconds}, Attachments={AttachmentCount}). Subject: {Subject}",
            toEmail,
            settings.Host,
            settings.Port,
            settings.EnableSsl,
            hasCredentials,
            settings.FromAddress,
            timeoutSeconds,
            attachmentCount,
            subject);

        using var message = new MailMessage
        {
            From = new MailAddress(settings.FromAddress, settings.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };
        message.To.Add(toEmail);

        var streams = new List<MemoryStream>();
        try
        {
            if (attachments is { Count: > 0 })
            {
                foreach (var attachment in attachments)
                {
                    var stream = new MemoryStream(attachment.Content);
                    streams.Add(stream);
                    message.Attachments.Add(new Attachment(stream, attachment.FileName, attachment.ContentType));
                }
            }

            using var client = new SmtpClient(settings.Host, settings.Port)
            {
                EnableSsl = settings.EnableSsl,
                Timeout = timeoutSeconds * 1000
            };

            if (hasCredentials)
                client.Credentials = new NetworkCredential(settings.Username, settings.Password);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var stopwatch = Stopwatch.StartNew();
            try
            {
                await client.SendMailAsync(message, timeoutCts.Token);
                stopwatch.Stop();
                logger.LogInformation(
                    "SMTP email sent to {Recipient} in {ElapsedMs}ms via {SmtpHost}:{SmtpPort}",
                    toEmail,
                    stopwatch.ElapsedMilliseconds,
                    settings.Host,
                    settings.Port);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                LogSendFailure(
                    ex,
                    toEmail,
                    settings,
                    stopwatch.ElapsedMilliseconds,
                    timeoutSeconds,
                    requestCanceled: cancellationToken.IsCancellationRequested,
                    smtpTimedOut: !cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested);
                throw;
            }
        }
        finally
        {
            foreach (var stream in streams)
                stream.Dispose();
        }
    }

    private void LogSendFailure(
        Exception ex,
        string toEmail,
        EmailSettings settings,
        long elapsedMs,
        int timeoutSeconds,
        bool requestCanceled,
        bool smtpTimedOut)
    {
        var reason = ClassifyFailure(ex, requestCanceled, smtpTimedOut, settings, timeoutSeconds);
        var smtpStatus = (ex as SmtpException)?.StatusCode.ToString()
            ?? FindInner<SmtpException>(ex)?.StatusCode.ToString()
            ?? "n/a";
        var socketError = FindInner<SocketException>(ex)?.SocketErrorCode.ToString() ?? "n/a";

        logger.LogError(
            ex,
            "SMTP email failed after {ElapsedMs}ms to {Recipient} via {SmtpHost}:{SmtpPort} (Ssl={EnableSsl}, Auth={HasCredentials}, From={FromAddress}). " +
            "Reason: {FailureReason}. RequestCanceled={RequestCanceled}. SmtpTimedOut={SmtpTimedOut}. SmtpStatus={SmtpStatus}. SocketError={SocketError}. ExceptionType={ExceptionType}",
            elapsedMs,
            toEmail,
            settings.Host,
            settings.Port,
            settings.EnableSsl,
            !string.IsNullOrWhiteSpace(settings.Username),
            settings.FromAddress,
            reason,
            requestCanceled,
            smtpTimedOut,
            smtpStatus,
            socketError,
            ex.GetType().FullName);
    }

    private static string ClassifyFailure(
        Exception ex,
        bool requestCanceled,
        bool smtpTimedOut,
        EmailSettings settings,
        int timeoutSeconds)
    {
        if (smtpTimedOut)
            return $"SMTP timed out after {timeoutSeconds}s connecting/sending via {settings.Host}:{settings.Port}. " +
                   $"Outbound SMTP is often blocked or blackholed on cloud hosts; try an HTTP email API (SendGrid/Resend), " +
                   $"or verify from the container: nc -zv {settings.Host} {settings.Port}.";

        if (ex is OperationCanceledException && requestCanceled)
            return "HTTP request was canceled while waiting for SMTP (client disconnect, proxy timeout, or host shutdown).";

        if (ex is OperationCanceledException)
            return "SMTP send was canceled. Check Email:Host reachability and Email:TimeoutSeconds.";

        if (ex is SmtpException smtp)
            return $"SMTP server rejected or failed the send (status {smtp.StatusCode}). For Gmail, use an App Password and allow SMTP.";

        var smtpInner = FindInner<SmtpException>(ex);
        if (smtpInner is not null)
            return $"SMTP server rejected or failed the send (status {smtpInner.StatusCode}). For Gmail, use an App Password and allow SMTP.";

        var socket = FindInner<SocketException>(ex);
        if (socket is not null)
            return $"Could not connect to SMTP host ({socket.SocketErrorCode}). Check Email:Host, Email:Port, firewall, and DNS.";

        if (ex is InvalidOperationException)
            return ex.Message;

        return $"Unexpected error during SMTP send: {ex.Message}";
    }

    private static T? FindInner<T>(Exception ex) where T : Exception
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is T match)
                return match;
        }

        return null;
    }
}
