namespace Platform.Shared.Dtos.Audit;

/// <summary>Counts over the whole audit log (not just the page or filters currently displayed).</summary>
public class AuditLogStatsDto
{
    public long TotalCount { get; set; }

    /// <summary>Entries written since 00:00 UTC today.</summary>
    public long TodayCount { get; set; }

    /// <summary>Suspensions, revocations and voids.</summary>
    public long SecurityFlagCount { get; set; }

    /// <summary>License / integration-key revocations.</summary>
    public long CriticalFlagCount { get; set; }
}
