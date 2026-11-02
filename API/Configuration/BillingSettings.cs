namespace Platform.Api.Configuration;

public enum RenewalExtendFrom
{
    /// <summary>From the old expiry (keeps the anniversary) unless it lapsed more than <see cref="BillingSettings.RenewalGraceDays"/>, then from the payment date.</summary>
    Auto,
    /// <summary>Always from the old expiry.</summary>
    PreviousExpiry,
    /// <summary>Always from the payment date.</summary>
    PaymentDate
}

public class BillingSettings
{
    public const string SectionName = "Billing";

    /// <summary>
    /// Currency used when a request leaves it empty (online pay links need GHS). Falls back to GHS when unset or invalid.
    /// </summary>
    public string DefaultCurrency { get; set; } = "GHS";

    public const string FallbackCurrency = "GHS";

    /// <summary>Master switch for the automatic renewal invoice worker.</summary>
    public bool RenewalInvoicesEnabled { get; set; } = true;

    /// <summary>Create and send the next-period invoice this many days before expiry (1–90).</summary>
    public int RenewalInvoiceDaysBefore { get; set; } = 14;

    /// <summary>Licenses that expired up to this many days ago still get a renewal invoice.</summary>
    public int RenewalLookbackDays { get; set; } = 30;

    public int RenewalPollMinutes { get; set; } = 60;

    public RenewalExtendFrom RenewalExtendFrom { get; set; } = RenewalExtendFrom.Auto;

    /// <summary>Used by <see cref="RenewalExtendFrom.Auto"/>.</summary>
    public int RenewalGraceDays { get; set; } = 7;

    /// <summary>Include a public "pay online" link in invoice emails/PDFs (needs Paystack and a GHS invoice).</summary>
    public bool PayLinkEnabled { get; set; } = true;

    public int PayLinkValidDays { get; set; } = 60;

    /// <summary>Optional dedicated HMAC key (32+ chars). Defaults to a key derived from Jwt:Key.</summary>
    public string? PayLinkSigningKey { get; set; }

    /// <summary>
    /// An invoice can't be voided while an online checkout started within this many minutes is still pending
    /// (the payer may be on the Paystack page). 0 turns the guard off.
    /// </summary>
    public int VoidBlockAfterCheckoutMinutes { get; set; } = 30;

    /// <summary>Length of a free trial in months (1–12).</summary>
    public int TrialMonths { get; set; } = 1;

    /// <summary>Send the trial-ending reminder (with the first invoice when the license has a price) this many days before the trial ends (1–60).</summary>
    public int TrialReminderDaysBefore { get; set; } = 7;

    /// <summary>"Extend trial" can't push the trial end past this many months after the trial started (1–24).</summary>
    public int TrialMaxTotalMonths { get; set; } = 3;
}
