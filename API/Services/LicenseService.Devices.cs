using Microsoft.EntityFrameworkCore;
using Platform.Api.Helpers;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public partial class LicenseService
{
    private const int MaxListedDevices = 200;

    public async Task<IReadOnlyList<LicenseDeviceDto>> ListDevicesAsync(string licenseId, CancellationToken cancellationToken = default)
    {
        if (!await db.Licenses.IgnoreQueryFilters().AnyAsync(l => l.Id == licenseId, cancellationToken))
            throw new NotFoundException("License not found.");

        var rows = await db.LicenseDevices.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.LicenseId == licenseId)
            .OrderBy(d => d.RevokedAt != null)
            .ThenByDescending(d => d.LastSeenAt)
            .Take(MaxListedDevices)
            .Select(d => new { d.Id, d.Name, d.DeviceIdHash, d.FirstSeenAt, d.LastSeenAt, d.RevokedAt })
            .ToListAsync(cancellationToken);

        return rows.Select(d => new LicenseDeviceDto
        {
            Id = d.Id,
            Name = d.Name,
            Fingerprint = d.DeviceIdHash[..8],
            FirstSeenAt = d.FirstSeenAt,
            LastSeenAt = d.LastSeenAt,
            RevokedAt = d.RevokedAt
        }).ToList();
    }

    /// <summary>Frees the device's slot. Idempotent: releasing an already released device is a no-op.</summary>
    public async Task<LicenseDeviceDto> ReleaseDeviceAsync(
        string licenseId,
        string deviceId,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var device = await db.LicenseDevices.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == deviceId && d.LicenseId == licenseId, cancellationToken)
            ?? throw new NotFoundException("Device not found.");

        if (device.RevokedAt is null)
        {
            var customerId = await db.Licenses.IgnoreQueryFilters().Where(l => l.Id == licenseId)
                .Select(l => l.CustomerId).FirstAsync(cancellationToken);
            device.RevokedAt = DateTime.UtcNow;
            // The audit writer saves the same context, so the release and its audit row commit together.
            await auditLog.WriteAsync(AuditAction.LicenseDeviceReleased, performedBy, customerId, licenseId, null,
                AuditJson.Serialize(new { deviceRowId = device.Id, name = device.Name }), ipAddress, cancellationToken);
            await db.SaveChangesAsync(cancellationToken); // no-op when the audit writer already saved
        }

        return new LicenseDeviceDto
        {
            Id = device.Id,
            Name = device.Name,
            Fingerprint = device.DeviceIdHash[..8],
            FirstSeenAt = device.FirstSeenAt,
            LastSeenAt = device.LastSeenAt,
            RevokedAt = device.RevokedAt
        };
    }
}
