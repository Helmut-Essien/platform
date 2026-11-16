namespace Platform.Shared.Dtos.Licenses;

public class ReleaseAllDevicesResultDto
{
    /// <summary>How many active devices were released (0 when none were active).</summary>
    public int ReleasedCount { get; set; }
}
