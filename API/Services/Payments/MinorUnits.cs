namespace Platform.Api.Services.Payments;

/// <summary>Exact decimal ↔ minor-unit (pesewa) conversion. Never goes through floating point.</summary>
public static class MinorUnits
{
    public static long FromAmount(decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be greater than zero.");

        var minor = amount * 100m;
        if (minor != decimal.Truncate(minor))
            throw new ArgumentException("Amount cannot have more than two decimal places.", nameof(amount));

        return checked((long)minor);
    }

    public static decimal ToAmount(long minor) => minor / 100m;
}
