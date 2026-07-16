using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;

namespace Platform.Api.Services.Email;

public class ResendEmailSender(
    IHttpClientFactory httpClientFactory,
    IOptions<EmailSettings> options,
    ILogger<ResendEmailSender> logger) : IEmailSender
{
    public const string HttpClientName = "Resend";

    public async Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;

        if (string.IsNullOrWhiteSpace(settings.ResendApiKey))
            throw new InvalidOperationException("Email:ResendApiKey is required when Email:Provider=Resend.");

        if (string.IsNullOrWhiteSpace(settings.FromAddress))
            throw new InvalidOperationException("Email:FromAddress is required when Email:Provider=Resend.");

        var from = string.IsNullOrWhiteSpace(settings.FromName)
            ? settings.FromAddress
            : $"{settings.FromName} <{settings.FromAddress}>";

        logger.LogInformation(
            "Sending Resend email to {Recipient} from {FromAddress}. Subject: {Subject}",
            toEmail,
            settings.FromAddress,
            subject);

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResendApiKey);
        request.Content = JsonContent.Create(new ResendSendRequest
        {
            From = from,
            To = [toEmail],
            Subject = subject,
            Html = htmlBody
        });

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            stopwatch.Stop();

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Resend email failed after {ElapsedMs}ms to {Recipient}. Status={StatusCode}. Response={ResponseBody}. CancellationRequested={CancellationRequested}",
                    stopwatch.ElapsedMilliseconds,
                    toEmail,
                    (int)response.StatusCode,
                    Truncate(body, 500),
                    cancellationToken.IsCancellationRequested);

                throw new InvalidOperationException(
                    $"Resend API returned {(int)response.StatusCode}: {Truncate(body, 300)}");
            }

            logger.LogInformation(
                "Resend email sent to {Recipient} in {ElapsedMs}ms. Response={ResponseBody}",
                toEmail,
                stopwatch.ElapsedMilliseconds,
                Truncate(body, 200));
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            logger.LogError(
                ex,
                "Resend email canceled after {ElapsedMs}ms to {Recipient} (request aborted). CancellationRequested=True",
                stopwatch.ElapsedMilliseconds,
                toEmail);
            throw;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            stopwatch.Stop();
            logger.LogError(
                ex,
                "Resend email failed after {ElapsedMs}ms to {Recipient}. ExceptionType={ExceptionType}. CancellationRequested={CancellationRequested}",
                stopwatch.ElapsedMilliseconds,
                toEmail,
                ex.GetType().FullName,
                cancellationToken.IsCancellationRequested);
            throw;
        }
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private sealed class ResendSendRequest
    {
        [JsonPropertyName("from")]
        public required string From { get; init; }

        [JsonPropertyName("to")]
        public required string[] To { get; init; }

        [JsonPropertyName("subject")]
        public required string Subject { get; init; }

        [JsonPropertyName("html")]
        public required string Html { get; init; }
    }
}
