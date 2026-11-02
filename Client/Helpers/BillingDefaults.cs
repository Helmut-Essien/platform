namespace Platform.Client.Helpers;

/// <summary>Client-side billing defaults. The server applies Billing:DefaultCurrency (GHS) to blank currencies.</summary>
public static class BillingDefaults
{
    /// <summary>Paystack (online pay links) only collects Ghana cedis.</summary>
    public const string Currency = "GHS";

    public const string NonPayLinkWarning =
        "Online pay links (Mobile Money / card) are only available for GHS invoices. The customer will have to pay this one offline.";

    /// <summary>True when the currency is set and is not GHS, so the invoice will have no pay-online link.</summary>
    public static bool LacksPayLink(string? currency) =>
        !string.IsNullOrWhiteSpace(currency)
        && !string.Equals(currency.Trim(), Currency, StringComparison.OrdinalIgnoreCase);
}
