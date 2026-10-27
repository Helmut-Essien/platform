using System.Net;
using System.Text;

namespace Platform.Client.Services;

/// <summary>
/// Innermost handler: turns transport failures (offline, DNS, CORS, request timeout) into a synthetic
/// 503 JSON response so callers see a normal API error instead of an unhandled <see cref="HttpRequestException"/>.
/// A cancellation requested by the caller still propagates as <see cref="OperationCanceledException"/>.
/// </summary>
public class NetworkFailureHandler : DelegatingHandler
{
    public const string UnreachableMessage = "Cannot reach the server. Check your connection and try again.";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return Unreachable(request);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient timeout, not the caller cancelling.
            return Unreachable(request);
        }
    }

    private static HttpResponseMessage Unreachable(HttpRequestMessage request) =>
        new(HttpStatusCode.ServiceUnavailable)
        {
            RequestMessage = request,
            ReasonPhrase = "Service Unavailable",
            Content = new StringContent(
                "{\"message\":\"" + UnreachableMessage + "\"}",
                Encoding.UTF8,
                "application/json")
        };
}
