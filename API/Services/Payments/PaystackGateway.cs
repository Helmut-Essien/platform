using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;

namespace Platform.Api.Services.Payments;

/// <summary>
/// Paystack REST client (typed HttpClient). The secret key is only ever placed in the Authorization
/// header of each request; it is never logged or included in exception messages.
/// </summary>
public sealed class PaystackGateway(HttpClient http, IOptions<PaystackOptions> options, ILogger<PaystackGateway> logger)
    : IPaymentGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<GatewayCheckout> InitializeAsync(GatewayCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        var body = new
        {
            email = request.Email,
            amount = request.AmountMinor.ToString(CultureInfo.InvariantCulture),
            currency = request.Currency,
            reference = request.Reference,
            callback_url = request.CallbackUrl,
            metadata = request.Metadata
        };

        using var message = CreateRequest(HttpMethod.Post, "transaction/initialize");
        message.Content = JsonContent.Create(body, options: Json);
        var envelope = await SendAsync<InitializeData>(message, "initialize", cancellationToken);

        if (envelope.Status != true || string.IsNullOrWhiteSpace(envelope.Data?.AuthorizationUrl))
            throw new PaymentGatewayException($"Paystack could not start the checkout: {Safe(envelope.Message)}");

        return new GatewayCheckout(envelope.Data.AuthorizationUrl, envelope.Data.AccessCode);
    }

    public async Task<GatewayVerification> VerifyAsync(string reference, CancellationToken cancellationToken = default)
    {
        using var message = CreateRequest(HttpMethod.Get, $"transaction/verify/{Uri.EscapeDataString(reference)}");
        PaystackEnvelope<VerifyData> envelope;
        try
        {
            envelope = await SendAsync<VerifyData>(message, "verify", cancellationToken);
        }
        catch (PaystackNotFoundException)
        {
            return new GatewayVerification(GatewayTransactionState.NotFound, 0, null, null, null, null, "Transaction reference not found.");
        }

        var data = envelope.Data;
        if (envelope.Status != true || data is null)
            throw new PaymentGatewayException($"Paystack verify failed: {Safe(envelope.Message)}");

        var state = (data.Status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "success" => GatewayTransactionState.Success,
            "failed" or "reversed" => GatewayTransactionState.Failed,
            _ => GatewayTransactionState.Pending
        };

        DateTime? paidAt = DateTimeOffset.TryParse(data.PaidAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;

        return new GatewayVerification(
            state,
            data.Amount,
            data.Currency?.Trim().ToUpperInvariant(),
            data.Channel?.Trim().ToLowerInvariant(),
            paidAt,
            data.Id?.ToString(CultureInfo.InvariantCulture),
            Safe(data.GatewayResponse),
            data.RequestedAmount);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var settings = options.Value;
        var baseUrl = (settings.BaseUrl ?? PaystackOptions.DefaultBaseUrl).TrimEnd('/') + "/";
        var message = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), path));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SecretKey?.Trim());
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return message;
    }

    private async Task<PaystackEnvelope<T>> SendAsync<T>(HttpRequestMessage message, string operation, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Paystack {Operation} request failed: {Error}", operation, ex.GetType().Name);
            throw new PaymentGatewayException("Paystack could not be reached. Try again shortly.", ex);
        }

        using (response)
        {
            PaystackEnvelope<T>? envelope = null;
            try
            {
                envelope = await response.Content.ReadFromJsonAsync<PaystackEnvelope<T>>(Json, cancellationToken);
            }
            catch (JsonException)
            {
                // fall through to the status-code handling below
            }

            if (response.IsSuccessStatusCode && envelope is not null)
                return envelope;

            // Paystack answers an unknown reference on verify with 400/404 + "Transaction reference not found".
            if (operation == "verify"
                && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound
                && envelope?.Message?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new PaystackNotFoundException();
            }

            logger.LogWarning(
                "Paystack {Operation} returned HTTP {StatusCode}: {Message}",
                operation, (int)response.StatusCode, Safe(envelope?.Message));

            var hint = response.StatusCode == HttpStatusCode.Unauthorized ? " Check Paystack__SecretKey." : string.Empty;
            throw new PaymentGatewayException(
                $"Paystack {operation} failed (HTTP {(int)response.StatusCode}): {Safe(envelope?.Message)}{hint}");
        }
    }

    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "no details";
        var trimmed = value.Trim();
        return trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }

    private sealed class PaystackNotFoundException : Exception;

    private sealed class PaystackEnvelope<T>
    {
        public bool? Status { get; set; }
        public string? Message { get; set; }
        public T? Data { get; set; }
    }

    private sealed class InitializeData
    {
        [JsonPropertyName("authorization_url")] public string? AuthorizationUrl { get; set; }
        [JsonPropertyName("access_code")] public string? AccessCode { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
    }

    private sealed class VerifyData
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        /// <summary>Amount before fees passed to the customer (Paystack "bearer: customer"); absent otherwise.</summary>
        [JsonPropertyName("requested_amount")] public long? RequestedAmount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("channel")] public string? Channel { get; set; }
        [JsonPropertyName("paid_at")] public string? PaidAt { get; set; }
        [JsonPropertyName("gateway_response")] public string? GatewayResponse { get; set; }
    }
}
