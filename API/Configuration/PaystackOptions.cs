namespace Platform.Api.Configuration;

/// <summary>
/// Paystack online payments. The feature is enabled only when <see cref="SecretKey"/> is set
/// (Paystack__SecretKey); without it the checkout endpoints return 503 and the UI hides the button.
/// </summary>
public class PaystackOptions
{
    public const string SectionName = "Paystack";

    public const string DefaultBaseUrl = "https://api.paystack.co";

    /// <summary>Secret key (sk_test_… or sk_live_…). Also the HMAC key for webhook signatures. Never logged.</summary>
    public string? SecretKey { get; set; }

    /// <summary>Optional public key (pk_…); not needed for the redirect checkout.</summary>
    public string? PublicKey { get; set; }

    public string BaseUrl { get; set; } = DefaultBaseUrl;

    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Lifetime collection cap shown on the dashboard (Paystack Starter Business in Ghana: GHS 30,000).
    /// Set to 0 to hide the dashboard card.
    /// </summary>
    public decimal CollectionCap { get; set; } = 30000m;

    /// <summary>How often the reconcile worker re-checks pending checkouts.</summary>
    public int ReconcileIntervalMinutes { get; set; } = 5;

    public bool Enabled => !string.IsNullOrWhiteSpace(SecretKey) && IsValidBaseUrl(BaseUrl);

    public bool IsTestMode => SecretKey?.Trim().StartsWith("sk_test_", StringComparison.Ordinal) == true;

    public static bool IsValidBaseUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback);
}
