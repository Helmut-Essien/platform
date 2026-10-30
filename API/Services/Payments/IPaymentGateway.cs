namespace Platform.Api.Services.Payments;

public interface IPaymentGateway
{
    /// <summary>Creates a hosted checkout. Throws <see cref="PaymentGatewayException"/> on provider/network errors.</summary>
    Task<GatewayCheckout> InitializeAsync(GatewayCheckoutRequest request, CancellationToken cancellationToken = default);

    /// <summary>Looks up a transaction by our reference. Throws <see cref="PaymentGatewayException"/> on provider/network errors.</summary>
    Task<GatewayVerification> VerifyAsync(string reference, CancellationToken cancellationToken = default);
}

public sealed record GatewayCheckoutRequest(
    string Reference,
    long AmountMinor,
    string Currency,
    string Email,
    string CallbackUrl,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record GatewayCheckout(string AuthorizationUrl, string? AccessCode);

public enum GatewayTransactionState
{
    /// <summary>Not paid yet (abandoned, ongoing, pending, processing, queued) — may still complete.</summary>
    Pending,
    Success,
    Failed,
    /// <summary>The provider does not know this reference (checkout page never opened, or wrong account).</summary>
    NotFound
}

public sealed record GatewayVerification(
    GatewayTransactionState State,
    long AmountMinor,
    string? Currency,
    string? Channel,
    DateTime? PaidAt,
    string? ProviderTransactionId,
    string? Message);

public sealed class PaymentGatewayException(string message, Exception? inner = null) : Exception(message, inner);
