using Platform.Shared.Dtos.ServiceProducts;
using Platform.Shared.Formatting;

namespace Platform.Client.Helpers;

/// <summary>Short price labels for the admin screens ("GHS 600.00 + GHS 90.00 tax / year").</summary>
public static class PriceText
{
    public static string Period(int months) => months switch
    {
        1 => "month",
        3 => "quarter",
        12 => "year",
        24 => "2 years",
        36 => "3 years",
        _ => $"{months} months"
    };

    public static string Format(decimal? amount, decimal tax, string? currency, int periodMonths)
    {
        if (amount is not > 0)
            return "No price";
        var taxText = tax > 0 ? $" + {MoneyText.Format(tax, currency)} tax" : "";
        return $"{MoneyText.Format(amount.Value, currency)}{taxText} / {Period(periodMonths)}";
    }

    public static string Format(PlanPriceDto? price) =>
        price is null ? "—" : Format(price.Amount, price.TaxAmount, price.Currency, price.PeriodMonths);
}
