namespace Platform.Shared.Dtos.Licenses;

/// <summary>Server trial settings, so the admin UI describes what will actually happen.</summary>
public class LicenseOptionsDto
{
    public int TrialMonths { get; set; } = 1;

    public int TrialReminderDaysBefore { get; set; } = 7;

    public int TrialMaxTotalMonths { get; set; } = 3;
}
