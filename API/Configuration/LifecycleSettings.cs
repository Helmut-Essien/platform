namespace Platform.Api.Configuration;

public class LifecycleSettings
{
    public const string SectionName = "Lifecycle";

    public bool ExpiryReminderEnabled { get; set; } = true;
    /// <summary>First (earliest) reminder, in days before expiry. Also the scan horizon.</summary>
    public int ExpiryReminderDays { get; set; } = 30;
    /// <summary>E5: further, closer reminders (days before expiry). Each step is sent at most once per expiry date.</summary>
    public int[] ExpiryReminderAdditionalSteps { get; set; } = [7, 1];
    public int ExpiryReminderPollHours { get; set; } = 24;
    public bool AutoSuspendOnOverdue { get; set; }
    public int OverduePollMinutes { get; set; } = 60;
}
