namespace Platform.Shared.Dtos.Licenses;

public class LicenseDeviceDto
{
    public required string Id { get; set; }

    public string? Name { get; set; }

    /// <summary>First characters of the stored device hash, to tell devices apart (the raw id is never stored).</summary>
    public required string Fingerprint { get; set; }

    public DateTime FirstSeenAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public bool IsActive => RevokedAt is null;
}
