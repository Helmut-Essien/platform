namespace Platform.Api.Configuration;

/// <summary>
/// Startup check of the Billing section (appsettings.json, overridable by Billing__* environment variables). Out-of-range
/// values are clamped or replaced where they are used; this makes those silent corrections visible in the log.
/// </summary>
internal static class BillingSettingsCheck
{
    internal static IReadOnlyList<string> Problems(BillingSettings settings)
    {
        var problems = new List<string>();
        var currency = settings.DefaultCurrency?.Trim() ?? string.Empty;
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
            problems.Add($"Billing:DefaultCurrency '{settings.DefaultCurrency}' is not a 3-letter code such as GHS; {BillingSettings.FallbackCurrency} is used.");

        var noticeDays = settings.PriceChangeNoticeDays;
        if (noticeDays is < 0 or > 365)
            problems.Add($"Billing:PriceChangeNoticeDays {noticeDays} is outside 0–365; {Math.Clamp(noticeDays, 0, 365)} is used.");

        var effectiveNotice = Math.Clamp(noticeDays, 0, 365);
        var invoiceDays = Math.Clamp(settings.RenewalInvoiceDaysBefore, 1, 90);
        if (effectiveNotice > 0 && settings.RenewalInvoicesEnabled && effectiveNotice < invoiceDays)
            problems.Add($"Billing:PriceChangeNoticeDays ({effectiveNotice}) is shorter than Billing:RenewalInvoiceDaysBefore ({invoiceDays}): "
                + "a price change taking effect at a renewal can reach the customer together with the invoice instead of ahead of it.");
        return problems;
    }
}
