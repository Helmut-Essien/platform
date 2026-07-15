namespace Platform.Api.Helpers;

public static class DateTimeNormalizer
{
    public static DateTime? ToUtc(DateTime? value) =>
        value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;
}
