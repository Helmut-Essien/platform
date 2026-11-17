using System.Globalization;

namespace Platform.Shared.Formatting;

/// <summary>
/// One money format for every screen, email and PDF: the ISO currency code, a space, then the amount with thousands
/// separators and two decimals, culture-independent ("GHS 1,200.00", "USD 50.00"). The code is unambiguous (a "$"
/// could be any dollar, "₵" is missing from many fonts), reads the same as Paystack and bank statements, and is plain
/// ASCII. Ghana cedis (GHS) are the primary currency; other codes are shown the same way.
/// </summary>
public static class MoneyText
{
    /// <summary>The primary currency (Ghana cedis). Online pay links (Paystack) only collect GHS.</summary>
    public const string PrimaryCurrency = "GHS";

    /// <summary>"GHS 1,200.00". A blank currency is shown as GHS; the code is upper-cased and trimmed.</summary>
    public static string Format(decimal amount, string? currency) =>
        $"{Code(currency)} {amount.ToString("N2", CultureInfo.InvariantCulture)}";

    /// <summary>The upper-cased, trimmed code, or GHS when blank.</summary>
    public static string Code(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? PrimaryCurrency : currency.Trim().ToUpperInvariant();
}
