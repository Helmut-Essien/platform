namespace Platform.Shared.Dtos.ServiceProducts;

public class RemapProductPlanResultDto
{
    public required string FromPlanName { get; set; }

    public required string ToCode { get; set; }

    public bool DryRun { get; set; }

    /// <summary>Licenses that were (or, for a dry run, would be) changed.</summary>
    public int UpdatedCount { get; set; }

    /// <summary>Revoked licenses with the old name; they are left as they are.</summary>
    public int SkippedRevokedCount { get; set; }
}
