namespace Platform.Shared.Enums;

/// <summary>Lifecycle of a scheduled price change on one license.</summary>
public enum LicensePriceChangeStatus
{
    /// <summary>Waiting for the first renewal invoice whose period starts on or after the effective date.</summary>
    Pending = 0,

    /// <summary>Used by a renewal invoice; the license's own price was updated at that point.</summary>
    Applied = 1,

    /// <summary>Cancelled by an admin, or superseded (manual price edit, plan change, another schedule, free/revoked license).</summary>
    Cancelled = 2
}
