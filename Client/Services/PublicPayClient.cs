using System.Net;
using System.Net.Http.Json;
using Platform.Shared.Dtos.Payments;

namespace Platform.Client.Services;

/// <summary>
/// HTTP client for the anonymous /pay/{token} page. Deliberately has none of the admin handlers (no JWT is
/// attached, a 401/404 never redirects to the admin login and no admin snackbars are raised).
/// </summary>
public sealed class PublicPayClient(HttpClient http)
{
    public enum Outcome { Ok, Invalid, Expired, Unavailable, RateLimited, Failed }

    public sealed record Result<T>(Outcome Outcome, T? Data, string? Message = null);

    public Task<Result<PublicInvoiceSummaryDto>> GetSummaryAsync(string token, CancellationToken ct = default) =>
        SendAsync<PublicInvoiceSummaryDto>(HttpMethod.Get, $"api/public/pay/{Uri.EscapeDataString(token)}", ct);

    public Task<Result<CheckoutSessionDto>> StartCheckoutAsync(string token, CancellationToken ct = default) =>
        SendAsync<CheckoutSessionDto>(HttpMethod.Post, $"api/public/pay/{Uri.EscapeDataString(token)}/checkout", ct);

    public Task<Result<PublicCheckoutStatusDto>> GetStatusAsync(string token, string? reference, CancellationToken ct = default) =>
        SendAsync<PublicCheckoutStatusDto>(HttpMethod.Get,
            $"api/public/pay/{Uri.EscapeDataString(token)}/status"
            + (string.IsNullOrWhiteSpace(reference) ? "" : $"?reference={Uri.EscapeDataString(reference)}"), ct);

    private async Task<Result<T>> SendAsync<T>(HttpMethod method, string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(new HttpRequestMessage(method, url), ct);
            if (response.IsSuccessStatusCode)
                return new(Outcome.Ok, await response.Content.ReadFromJsonAsync<T>(ct));
            var message = await ReadDetailAsync(response, ct);
            return response.StatusCode switch
            {
                HttpStatusCode.NotFound => new(Outcome.Invalid, default, message),
                HttpStatusCode.Gone => new(Outcome.Expired, default, message),
                HttpStatusCode.ServiceUnavailable => new(Outcome.Unavailable, default, message),
                HttpStatusCode.TooManyRequests => new(Outcome.RateLimited, default, "Too many attempts. Please wait a minute and try again."),
                _ => new(Outcome.Failed, default, message ?? "Something went wrong. Please try again.")
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new(Outcome.Failed, default, "We couldn't reach the server. Check your connection and try again.");
        }
    }

    private static async Task<string?> ReadDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(ct);
            return problem?.Detail;
        }
        catch
        {
            return null;
        }
    }

    private sealed class ProblemBody
    {
        public string? Detail { get; set; }
    }
}
