namespace Platform.Api.Helpers;

public static class DateTimeNormalizer
{
    public static DateTime? ToUtc(DateTime? value) =>
        value.HasValue ? ToUtc(value.Value) : null;

    public static DateTime ToUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    /// <summary>
    /// A calendar date (midnight) remains valid through that UTC day.
    /// A value with a time expires at that instant.
    /// </summary>
    public static bool IsPastExpiry(DateTime? expiresAt, DateTime utcNow)
    {
        if (!expiresAt.HasValue)
            return false;

        var expiry = ToUtc(expiresAt.Value);
        if (expiry.TimeOfDay == TimeSpan.Zero)
            return utcNow.Date > expiry.Date;

        return expiry <= utcNow;
    }

    /// <summary>
    /// A calendar due date (midnight) becomes overdue on the following UTC day.
    /// A value with a time is overdue after that instant.
    /// </summary>
    public static bool IsPastDue(DateTime? dueDate, DateTime utcNow)
    {
        if (!dueDate.HasValue)
            return false;

        var due = ToUtc(dueDate.Value);
        if (due >= utcNow)
            return false;

        if (due.TimeOfDay == TimeSpan.Zero && due.Date == utcNow.Date)
            return false;

        return true;
    }
}
