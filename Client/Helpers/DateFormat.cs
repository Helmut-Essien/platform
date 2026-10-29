using System.Globalization;

namespace Platform.Client.Helpers;

/// <summary>
/// The app's single date/time format.
/// <list type="bullet">
/// <item><see cref="Date(System.DateTime)"/>: calendar dates (issue, due, expiry, created day), shown as stored (UTC day),
/// so they match the server's business rules ("Oct 29, 2026").</item>
/// <item><see cref="DateAndTime"/> / <see cref="Timestamp"/>: moments, converted to the viewer's local time zone
/// ("Oct 29, 2026 14:05").</item>
/// </list>
/// </summary>
public static class DateFormat
{
    public const string DatePattern = "MMM d, yyyy";
    public const string DateTimePattern = "MMM d, yyyy HH:mm";
    public const string TimestampPattern = "MMM d, yyyy HH:mm:ss";
    public const string Missing = "—";

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Date(DateTime value) => value.ToString(DatePattern, Culture);

    public static string Date(DateTime? value) => value is null ? Missing : Date(value.Value);

    public static string DateAndTime(DateTime value, TimeZoneInfo? zone = null) =>
        ToZone(value, zone).ToString(DateTimePattern, Culture);

    public static string DateAndTime(DateTime? value, TimeZoneInfo? zone = null) =>
        value is null ? Missing : DateAndTime(value.Value, zone);

    /// <summary>Local time with seconds, for audit-style logs.</summary>
    public static string Timestamp(DateTime value, TimeZoneInfo? zone = null) =>
        ToZone(value, zone).ToString(TimestampPattern, Culture);

    /// <summary>API timestamps are UTC; an unspecified kind is treated as UTC.</summary>
    public static DateTime ToZone(DateTime value, TimeZoneInfo? zone = null)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return TimeZoneInfo.ConvertTimeFromUtc(utc, zone ?? TimeZoneInfo.Local);
    }
}
