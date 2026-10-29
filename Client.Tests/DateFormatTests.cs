using Platform.Client.Helpers;
using Xunit;

namespace Client.Tests;

public class DateFormatTests
{
    private static readonly TimeZoneInfo PlusTwo = TimeZoneInfo.CreateCustomTimeZone("T+2", TimeSpan.FromHours(2), "T+2", "T+2");

    [Fact]
    public void Date_UsesSharedPattern_WithoutTimeZoneShift()
    {
        var value = new DateTime(2026, 10, 29, 23, 30, 0, DateTimeKind.Utc);
        Assert.Equal("Oct 29, 2026", DateFormat.Date(value));
    }

    [Fact]
    public void Date_Null_IsDash() => Assert.Equal("—", DateFormat.Date((DateTime?)null));

    [Fact]
    public void DateAndTime_ConvertsUtcToZone()
    {
        var value = new DateTime(2026, 10, 29, 23, 30, 0, DateTimeKind.Utc);
        Assert.Equal("Oct 30, 2026 01:30", DateFormat.DateAndTime(value, PlusTwo));
    }

    [Fact]
    public void UnspecifiedKind_IsTreatedAsUtc()
    {
        var value = new DateTime(2026, 1, 5, 8, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal("Jan 5, 2026 10:00:00", DateFormat.Timestamp(value, PlusTwo));
    }

    [Fact]
    public void DateAndTime_Null_IsDash() => Assert.Equal("—", DateFormat.DateAndTime((DateTime?)null));
}
