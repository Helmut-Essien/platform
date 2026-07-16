using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;

namespace Platform.Api.Services.Email;

public class SmtpEmailSender(IOptions<EmailSettings> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        if (string.IsNullOrWhiteSpace(settings.Host))
            throw new InvalidOperationException("Email:Host is required for SMTP provider.");

        var hasCredentials = !string.IsNullOrWhiteSpace(settings.Username);
        logger.LogInformation(
            "Sending SMTP email to {Recipient} via {SmtpHost}:{SmtpPort} (Ssl={EnableSsl}, Auth={HasCredentials}, From={FromAddress}). Subject: {Subject}",
            toEmail,
            settings.Host,
            settings.Port,
            settings.EnableSsl,
            hasCredentials,
            settings.FromAddress,
            subject);

        using var message = new MailMessage
        {
            From = new MailAddress(settings.FromAddress, settings.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };
        message.To.Add(toEmail);

        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.EnableSsl
        };

        if (hasCredentials)
            client.Credentials = new NetworkCredential(settings.Username, settings.Password);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await client.SendMailAsync(message, cancellationToken);
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
            LogSendFailure(ex, toEmail, settings, stopwatch.ElapsedMilliseconds, cancellationToken);
            throw;
        }
    }

    private void LogSendFailure(
        Exception ex,
        string toEmail,
        EmailSettings settings,
        long elapsedMs,
        CancellationToken cancellationToken)
    {
        var reason = ClassifyFailure(ex, cancellationToken);
        var smtpStatus = (ex as SmtpException)?.StatusCode.ToString()
            ?? FindInner<SmtpException>(ex)?.StatusCode.ToString()
            ?? "n/a";
        var socketError = FindInner<SocketException>(ex)?.SocketErrorCode.ToString() ?? "n/a";

        logger.LogError(
            ex,
            "SMTP email failed after {ElapsedMs}ms to {Recipient} via {SmtpHost}:{SmtpPort} (Ssl={EnableSsl}, Auth={HasCredentials}, From={FromAddress}). " +
            "Reason: {FailureReason}. CancellationRequested={CancellationRequested}. SmtpStatus={SmtpStatus}. SocketError={SocketError}. ExceptionType={ExceptionType}",
            elapsedMs,
            toEmail,
            settings.Host,
            settings.Port,
            settings.EnableSsl,
            !string.IsNullOrWhiteSpace(settings.Username),
            settings.FromAddress,
            reason,
            cancellationToken.IsCancellationRequested,
            smtpStatus,
            socketError,
            ex.GetType().FullName);
    }

    private static string ClassifyFailure(Exception ex, CancellationToken cancellationToken)
    {
        if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            return "Request was canceled while waiting for SMTP (client disconnect, proxy timeout, or host shutdown). SMTP may be unreachable or too slow.";

        if (ex is OperationCanceledException)
            return "SMTP send was canceled (token not request-linked). Check SMTP host reachability and timeouts.";

        if (ex is SmtpException smtp)
            return $"SMTP server rejected or failed the send (status {smtp.StatusCode}).";

        var smtpInner = FindInner<SmtpException>(ex);
        if (smtpInner is not null)
            return $"SMTP server rejected or failed the send (status {smtpInner.StatusCode}).";

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
