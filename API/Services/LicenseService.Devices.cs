using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
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

    /// <summary>
    /// Frees the device's slot. Idempotent: releasing an already released device is a no-op (no second audit row).
    /// Runs under the same license row lock the activation path takes, so a release can't interleave with a
    /// concurrent first check or another admin's release of the same device. The next validation from that device
    /// simply re-registers it (taking a slot if one is free); nothing is cached that would need clearing, because
    /// validation reads the device table from PostgreSQL on every check.
    /// </summary>
    public Task<LicenseDeviceDto> ReleaseDeviceAsync(
        string licenseId,
        string deviceId,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default) =>
        RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
            var customerId = await db.Licenses.IgnoreQueryFilters().Where(l => l.Id == licenseId)
                .Select(l => l.CustomerId).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("License not found.");
            await LockLicenseRowAsync(licenseId, ct);

            var device = await db.LicenseDevices.IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.Id == deviceId && d.LicenseId == licenseId, ct)
                ?? throw new NotFoundException("Device not found.");

            if (device.RevokedAt is null)
            {
                device.RevokedAt = DateTime.UtcNow;
                // The audit writer saves the same context, so the release and its audit row commit together.
                await auditLog.WriteAsync(AuditAction.LicenseDeviceReleased, performedBy, customerId, licenseId, null,
                    AuditJson.Serialize(new { deviceRowId = device.Id, name = device.Name }), ipAddress, ct);
                await db.SaveChangesAsync(ct); // no-op when the audit writer already saved
            }

            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return ToDto(device);
        }, cancellationToken);

    /// <summary>
    /// Frees every active device of the license in one transaction (one audit row per device). Returns how many
    /// were released; calling it again returns 0 and writes nothing.
    /// </summary>
    public Task<int> ReleaseAllDevicesAsync(
        string licenseId,
        string performedBy,
        string? ipAddress = null,
        CancellationToken cancellationToken = default) =>
        RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
            var customerId = await db.Licenses.IgnoreQueryFilters().Where(l => l.Id == licenseId)
                .Select(l => l.CustomerId).FirstOrDefaultAsync(ct)
                ?? throw new NotFoundException("License not found.");
            await LockLicenseRowAsync(licenseId, ct);

            var active = await db.LicenseDevices.IgnoreQueryFilters()
                .Where(d => d.LicenseId == licenseId && d.RevokedAt == null)
                .ToListAsync(ct);
            if (active.Count > 0)
            {
                var now = DateTime.UtcNow;
                var audits = new List<AuditEntryRequest>(active.Count);
                foreach (var device in active)
                {
                    device.RevokedAt = now;
                    audits.Add(new AuditEntryRequest(AuditAction.LicenseDeviceReleased, performedBy, customerId, licenseId, null,
                        AuditJson.Serialize(new { deviceRowId = device.Id, name = device.Name, releasedAll = true }), ipAddress));
                }
                await auditLog.WriteBatchAsync(audits, ct);
                await db.SaveChangesAsync(ct); // no-op when the audit writer already saved
            }

            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return active.Count;
        }, cancellationToken);

    private static LicenseDeviceDto ToDto(Platform.Api.Entities.LicenseDevice device) => new()
    {
        Id = device.Id,
        Name = device.Name,
        Fingerprint = device.DeviceIdHash[..8],
        FirstSeenAt = device.FirstSeenAt,
        LastSeenAt = device.LastSeenAt,
        RevokedAt = device.RevokedAt
    };
}
