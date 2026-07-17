namespace Platform.Api.Configuration;

public class LifecycleSettings
{
    public const string SectionName = "Lifecycle";

    public bool ExpiryReminderEnabled { get; set; } = true;
    public int ExpiryReminderDays { get; set; } = 30;
    public int ExpiryReminderPollHours { get; set; } = 24;
    public bool AutoSuspendOnOverdue { get; set; }
    public int OverduePollMinutes { get; set; } = 60;
}
