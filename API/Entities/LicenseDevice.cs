namespace Platform.Api.Entities;

/// <summary>A machine/installation activated against a license (device limit). The raw device id is never stored.</summary>
public class LicenseDevice
{
    public string Id { get; set; } = NUlid.Ulid.NewUlid().ToString();

    public required string LicenseId { get; set; }

    /// <summary>SHA-256 hex of the client-supplied device id (see LicenseDeviceHasher).</summary>
    public required string DeviceIdHash { get; set; }

    public string? Name { get; set; }

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>Updated at most every Licensing:DeviceLastSeenUpdateMinutes to avoid a write per check.</summary>
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set when the admin releases the device; it frees a slot. The same device re-activates if a slot is free.</summary>
    public DateTime? RevokedAt { get; set; }

    public License License { get; set; } = null!;
}
