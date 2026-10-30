namespace Platform.Api.Services.Renewals;

/// <summary>Calendar period arithmetic for renewals. All values are UTC; the time of day is preserved.</summary>
public static class PeriodMath
{
    /// <summary>
    /// Adds whole months and lands on <paramref name="anchorDay"/>, clamped to the target month's length:
    /// anchor 31 gives Jan 31 → Feb 28/29 → Mar 31; Feb 29 + 12 months → Feb 28 (anchor 29 → Feb 29 again in leap years).
    /// </summary>
    public static DateTime AddMonths(DateTime start, int months, int? anchorDay = null)
    {
        if (months is < 1 or > 1200)
            throw new ArgumentOutOfRangeException(nameof(months));
        var utc = start.Kind == DateTimeKind.Utc ? start : DateTime.SpecifyKind(start, DateTimeKind.Utc);
        var target = utc.AddMonths(months);
        var anchor = Math.Clamp(anchorDay ?? utc.Day, 1, 31);
        var day = Math.Min(anchor, DateTime.DaysInMonth(target.Year, target.Month));
        return new DateTime(target.Year, target.Month, day, 0, 0, 0, DateTimeKind.Utc) + utc.TimeOfDay;
    }
}
