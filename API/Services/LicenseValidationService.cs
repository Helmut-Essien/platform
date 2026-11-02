using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Helpers;
using Platform.Api.Security;
using Platform.Api.Services.Licensing;
using Platform.Shared.Dtos.Licenses;
using Platform.Shared.Enums;
using R = Platform.Shared.Dtos.Licenses.LicenseValidationReasons;

namespace Platform.Api.Services;

/// <summary>
/// Validates a license key for a product. PostgreSQL is the only source of truth: a warm check is one indexed
/// round trip (integration keys are cached in memory, Redis is not on the hot path), so Redis being down has no
/// effect and a database outage surfaces as <see cref="LicenseValidationUnavailableException"/> (503), never as
/// "invalid". Valid answers carry a signed offline token when a signing key is configured.
/// </summary>
public class LicenseValidationService(
    AppDbContext db,
    IntegrationKeyCache integrationKeyCache,
    LicenseTokenSigner tokenSigner,
    IOptions<LicensingSettings> licensingSettings,
    IOptions<RedisSettings> redisSettings,
    ILogger<LicenseValidationService> logger,
    TimeProvider? clock = null) : ILicenseValidationService, IAsyncDisposable
{
    private readonly LicensingSettings _settings = licensingSettings.Value;
    private readonly RedisSettings _redisSettings = redisSettings.Value;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private Task? _abandoned;

    public async Task<ValidateLicenseResponse> ValidateAsync(
        string integrationKeyHeader,
        ValidateLicenseRequest request,
        CancellationToken cancellationToken = default)
    {
        var limit = TimeSpan.FromSeconds(Math.Clamp(_settings.ValidationTimeoutSeconds, 1, 60));
        Task<ValidateLicenseResponse>? work = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        try
        {
            // Cooperative cancellation alone is not enough: against a frozen server Npgsql's cancel request can
            // block far longer than the limit. WaitAsync is a hard deadline for the caller; the abandoned query
            // still sees the cancelled token and ends on its own.
            work = ValidateCoreAsync(integrationKeyHeader, request, timeout.Token);
            return await work.WaitAsync(limit + TimeSpan.FromSeconds(1), cancellationToken);
        }
        catch (TimeoutException ex) when (!cancellationToken.IsCancellationRequested && work is { IsCompleted: false })
        {
            _abandoned = work; // still using the DbContext: DisposeAsync waits for it before the scope disposes db
            logger.LogWarning(ex, "License validation exceeded its {Seconds}s deadline", _settings.ValidationTimeoutSeconds);
            throw new LicenseValidationUnavailableException("License validation timed out.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "License validation timed out after {Seconds}s", _settings.ValidationTimeoutSeconds);
            throw new LicenseValidationUnavailableException("License validation timed out.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not LicenseValidationUnavailableException
                                   && LicenseValidationUnavailableException.IsInfrastructureFailure(ex))
        {
            logger.LogError(ex, "License validation unavailable: database or network failure");
            throw new LicenseValidationUnavailableException("License validation is temporarily unavailable.", ex);
        }
    }

    /// <summary>
    /// Runs when the request scope ends (after the response was sent) and before the scope disposes the
    /// DbContext, so a query abandoned at the deadline never races the context's disposal.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_abandoned is not { } task)
            return;
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(120));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Abandoned license validation ended with an error");
        }
        GC.SuppressFinalize(this);
    }

    private async Task<ValidateLicenseResponse> ValidateCoreAsync(
        string integrationKeyHeader,
        ValidateLicenseRequest request,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        if (string.IsNullOrWhiteSpace(integrationKeyHeader))
            return Invalid(R.InvalidRequest, "Integration key is required.", now);

        if (string.IsNullOrWhiteSpace(request.LicenseKey))
            return Invalid(R.InvalidRequest, "License key is required.", now);
        // Copy/pasted keys often carry stray whitespace; generated keys never contain any.
        var licenseKey = request.LicenseKey.Trim();

        var deviceId = string.IsNullOrWhiteSpace(request.DeviceId) ? null : request.DeviceId.Trim();
        if (deviceId is { Length: > 200 })
            return Invalid(R.InvalidRequest, "Device id must be 200 characters or less.", now);
        if (deviceId is null && _settings.RequireDeviceId)
            return Invalid(R.DeviceRequired, "A device id is required.", now);

        var integration = await ResolveIntegrationKeyAsync(integrationKeyHeader.Trim(), ct);
        if (integration is null)
            return Invalid(R.InvalidIntegrationKey, "Invalid integration key.", now);

        // An integration key belongs to exactly one product: a mismatching serviceCode is a client misconfiguration
        // (reported as wrong_service, before any license lookup), not an unknown integration key.
        if (!string.IsNullOrWhiteSpace(request.ServiceCode)
            && !string.Equals(integration.ServiceProductCode, request.ServiceCode.Trim(), StringComparison.OrdinalIgnoreCase))
            return Invalid(R.WrongService, "License does not match the requested service.", now);

        var lookupHash = KeyLookupHasher.ComputeSha256Hex(licenseKey);
        var deviceHash = deviceId is null ? null : LicenseDeviceHasher.Hash(deviceId);

        // Single round trip: license + customer flag + (when a device is sent) its device row and active count.
        var baseQuery = db.Licenses
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(l => l.ServiceProductId == integration.ServiceProductId
                && l.LicenseKeyHash != null
                && l.LicenseKeyLookupHash == lookupHash);

        var candidates = deviceHash is null
            ? await baseQuery.Select(l => new Candidate
            {
                Id = l.Id, CustomerId = l.CustomerId, KeyHash = l.LicenseKeyHash!, Status = l.Status, ExpiresAt = l.ExpiresAt,
                PlanName = l.PlanName, MaxDevices = l.MaxDevices, IsTrial = l.IsTrial, IsComplimentary = l.IsComplimentary,
                CustomerSuspended = l.Customer.IsSuspended
            }).ToListAsync(ct)
            : await baseQuery.Select(l => new Candidate
            {
                Id = l.Id, CustomerId = l.CustomerId, KeyHash = l.LicenseKeyHash!, Status = l.Status, ExpiresAt = l.ExpiresAt,
                PlanName = l.PlanName, MaxDevices = l.MaxDevices, IsTrial = l.IsTrial, IsComplimentary = l.IsComplimentary,
                CustomerSuspended = l.Customer.IsSuspended,
                Device = l.Devices.Where(d => d.DeviceIdHash == deviceHash)
                    .Select(d => new DeviceRow { Id = d.Id, Name = d.Name, LastSeenAt = d.LastSeenAt, RevokedAt = d.RevokedAt })
                    .FirstOrDefault(),
                ActiveDevices = l.Devices.Count(d => d.RevokedAt == null)
            }).ToListAsync(ct);

        Candidate? license = null;
        var needsHashUpgrade = false;
        foreach (var candidate in candidates)
        {
            if (KeyHasher.Verify(licenseKey, candidate.KeyHash, out var upgrade))
            {
                license = candidate;
                needsHashUpgrade = upgrade;
                break;
            }
        }

        if (license is null)
            return Invalid(R.InvalidKey, "Invalid license key.", now);

        if (needsHashUpgrade)
            await UpgradeLicenseKeyHashAsync(license.Id, lookupHash, license.KeyHash, licenseKey, ct);

        // Only a caller holding the real key learns why it is refused.
        var refusal = license switch
        {
            { CustomerSuspended: true } => (R.Suspended, "License is suspended."),
            { Status: LicenseStatus.Revoked } => (R.Revoked, "License has been revoked."),
            { Status: LicenseStatus.Suspended } => (R.Suspended, "License is suspended."),
            { Status: LicenseStatus.Pending } => (R.NotActive, "License is not active yet."),
            { Status: LicenseStatus.Expired } => (R.Expired, "License has expired."),
            _ when DateTimeNormalizer.IsPastExpiry(license.ExpiresAt, now) => (R.Expired, "License has expired."),
            _ => default((string, string)?)
        };
        if (refusal is { } r)
            return Invalid(r.Item1, r.Item2, now, license.Id);

        int? activeDevices = null;
        if (deviceHash is not null)
        {
            var outcome = await TouchOrActivateDeviceAsync(license, deviceHash, request.DeviceName, now, ct);
            if (outcome is null)
                return Invalid(R.DeviceLimit,
                    $"This license is already active on its maximum of {license.MaxDevices} device(s). Release a device to use it here.",
                    now, license.Id, license.MaxDevices, license.ActiveDevices);
            activeDevices = outcome;
        }

        await TouchIntegrationKeyLastUsedAsync(integration, now, ct);

        var graceUntil = GraceUntil(license.ExpiresAt, now);
        var refreshAfter = RefreshAfter(now, graceUntil);
        var token = tokenSigner.Sign(new LicenseTokenClaims(
            license.Id, license.CustomerId, integration.ServiceProductCode, license.PlanName, license.ExpiresAt,
            deviceId, license.IsTrial, license.IsComplimentary, now, refreshAfter, graceUntil));

        return new ValidateLicenseResponse
        {
            IsValid = true,
            Reason = R.Ok,
            PlanName = license.PlanName,
            ExpiresAt = license.ExpiresAt,
            LicenseId = license.Id,
            ServerTime = now,
            Token = token,
            RefreshAfter = refreshAfter,
            OfflineGraceUntil = graceUntil,
            MaxDevices = deviceHash is null ? null : license.MaxDevices,
            ActiveDevices = activeDevices
        };
    }

    /// <summary>
    /// Offline grace: now + OfflineGraceDays, but never past the license's own end (+ ExpiryGraceHours). A
    /// date-only expiry is valid through that whole UTC day, matching <see cref="DateTimeNormalizer.IsPastExpiry"/>.
    /// </summary>
    internal DateTime GraceUntil(DateTime? expiresAt, DateTime now)
    {
        var grace = now.AddDays(Math.Clamp(_settings.OfflineGraceDays, 1, 90));
        if (expiresAt is { } e)
        {
            var expiry = DateTimeNormalizer.ToUtc(e);
            var end = expiry.TimeOfDay == TimeSpan.Zero ? expiry.Date.AddDays(1) : expiry;
            end = end.AddHours(Math.Clamp(_settings.ExpiryGraceHours, 0, 168));
            if (end < grace)
                grace = end;
        }
        return grace > now ? grace : now.AddMinutes(1);
    }

    internal DateTime RefreshAfter(DateTime now, DateTime graceUntil)
    {
        var refresh = now.AddHours(Math.Clamp(_settings.TokenRefreshHours, 1, 168));
        var half = now + TimeSpan.FromTicks((graceUntil - now).Ticks / 2);
        return refresh < half ? refresh : half;
    }

    /// <summary>
    /// Returns the active device count after the check, or null when the device limit refuses a new device.
    /// Known active devices cost nothing (LastSeen is throttled). New/released devices are activated under a
    /// row lock on the license so concurrent first checks can never exceed MaxDevices.
    /// </summary>
    private async Task<int?> TouchOrActivateDeviceAsync(
        Candidate license, string deviceHash, string? deviceName, DateTime now, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim()[..Math.Min(deviceName.Trim().Length, 100)];

        if (license.Device is { RevokedAt: null } known)
        {
            var throttle = TimeSpan.FromMinutes(Math.Clamp(_settings.DeviceLastSeenUpdateMinutes, 1, 1440));
            if (now - known.LastSeenAt >= throttle || (name is not null && known.Name != name))
            {
                try
                {
                    if (db.Database.IsRelational())
                    {
                        await db.LicenseDevices.IgnoreQueryFilters()
                            .Where(d => d.Id == known.Id)
                            .ExecuteUpdateAsync(u => u
                                .SetProperty(d => d.LastSeenAt, now)
                                .SetProperty(d => d.Name, d => name ?? d.Name), ct);
                    }
                    else
                    {
                        var row = await db.LicenseDevices.IgnoreQueryFilters().FirstAsync(d => d.Id == known.Id, ct);
                        row.LastSeenAt = now;
                        row.Name = name ?? row.Name;
                        await db.SaveChangesAsync(ct);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Bookkeeping only: a failed LastSeen write must not refuse a valid license.
                    logger.LogWarning(ex, "Failed to update LastSeen for device {DeviceId}", known.Id);
                }
            }
            return license.ActiveDevices;
        }

        // Fast refusal without taking a lock; re-checked under the lock below.
        if (license.MaxDevices is { } max && license.ActiveDevices >= max)
            return null;

        LicenseDevice? pendingDevice = null;
        AuditLog? pendingAudit = null;
        void DetachPending()
        {
            // A retried attempt (or a failure) must not leave the previous attempt's entities tracked.
            if (pendingDevice is not null)
                db.Entry(pendingDevice).State = EntityState.Detached;
            if (pendingAudit is not null)
                db.Entry(pendingAudit).State = EntityState.Detached;
            pendingDevice = null;
            pendingAudit = null;
        }

        try
        {
            return await RetriableTransaction.ExecuteAsync(db, async (tx, token) =>
            {
                DetachPending();
                if (db.Database.IsRelational())
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"""SELECT 1 FROM "Licenses" WHERE "Id" = {license.Id} FOR UPDATE""", token);

                var device = await db.LicenseDevices.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(d => d.LicenseId == license.Id && d.DeviceIdHash == deviceHash, token);
                var active = await db.LicenseDevices.IgnoreQueryFilters()
                    .CountAsync(d => d.LicenseId == license.Id && d.RevokedAt == null, token);

                if (device is { RevokedAt: null })
                    return (int?)active; // a concurrent first check activated it

                var limit = await db.Licenses.IgnoreQueryFilters().Where(l => l.Id == license.Id)
                    .Select(l => l.MaxDevices).FirstAsync(token);
                if (limit is { } m && active >= m)
                {
                    license.ActiveDevices = active;
                    license.MaxDevices = limit;
                    return null;
                }

                if (device is null)
                {
                    device = new LicenseDevice
                    {
                        LicenseId = license.Id, DeviceIdHash = deviceHash, Name = name, FirstSeenAt = now, LastSeenAt = now
                    };
                    db.LicenseDevices.Add(device);
                }
                else
                {
                    device.RevokedAt = null;
                    device.LastSeenAt = now;
                    device.Name = name ?? device.Name;
                }
                pendingDevice = device;

                var audit = new AuditLog
                {
                    Action = AuditAction.LicenseDeviceActivated,
                    PerformedBy = "license-validation",
                    CustomerId = license.CustomerId,
                    LicenseId = license.Id,
                    DetailsJson = AuditJson.Serialize(new { deviceRowId = device.Id, name = device.Name, activeDevices = active + 1, maxDevices = limit }),
                    Timestamp = now
                };
                db.AuditLogs.Add(audit);
                pendingAudit = audit;
                await db.SaveChangesAsync(token);
                if (tx is not null)
                    await tx.CommitAsync(token);
                return active + 1;
            }, ct);
        }
        finally
        {
            DetachPending();
        }
    }

    private async Task<IntegrationKeyCache.Entry?> ResolveIntegrationKeyAsync(
        string plainIntegrationKey,
        CancellationToken cancellationToken)
    {
        var lookupHash = KeyLookupHasher.ComputeSha256Hex(plainIntegrationKey);
        var cacheKey = IntegrationKeyCache.KeyOf(lookupHash, null);
        if (integrationKeyCache.TryGet(cacheKey, out var cached))
            return cached;
        var generation = integrationKeyCache.Generation;

        IQueryable<IntegrationKey> query = db.IntegrationKeys
            .AsNoTracking()
            .Where(k => k.IsActive && k.KeyLookupHash == lookupHash);

        var candidates = await query
            .Select(k => new { k.Id, k.KeyHash, k.ServiceProductId, k.ServiceProduct.Code, k.LastUsedAt })
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            if (KeyHasher.Verify(plainIntegrationKey, candidate.KeyHash, out var upgrade))
            {
                if (upgrade)
                    await UpgradeIntegrationKeyHashAsync(candidate.Id, candidate.KeyHash, plainIntegrationKey, cancellationToken);
                var entry = new IntegrationKeyCache.Entry(candidate.Id, candidate.ServiceProductId, candidate.Code, candidate.LastUsedAt);
                integrationKeyCache.Set(cacheKey, entry, generation);
                return entry;
            }
        }

        return null;
    }

    private async Task TouchIntegrationKeyLastUsedAsync(IntegrationKeyCache.Entry entry, DateTime now, CancellationToken ct)
    {
        var threshold = now.AddMinutes(-_redisSettings.IntegrationKeyLastUsedUpdateMinutes);
        if (entry.LastUsedAt.HasValue && entry.LastUsedAt.Value >= threshold)
            return;

        try
        {
            await db.IntegrationKeys
                .Where(k => k.Id == entry.IntegrationKeyId)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), ct);
            entry.LastUsedAt = now; // shared cached instance: stops repeat writes until the next threshold
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to update LastUsedAt for integration key {IntegrationKeyId}", entry.IntegrationKeyId);
        }
    }

    // Legacy BCrypt hashes are rewritten once, on first successful use, so later calls are cheap.
    // The rewrite is a compare-and-set on the old hash (ExecuteUpdate): concurrent validations of the same legacy
    // key cannot clobber each other, a concurrent rotation is never overwritten, and no license Version bump.
    private async Task UpgradeIntegrationKeyHashAsync(
        string integrationKeyId,
        string oldHash,
        string plainKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var newHash = KeyHasher.Hash(plainKey);
            if (db.Database.IsRelational())
            {
                await db.IntegrationKeys
                    .Where(k => k.Id == integrationKeyId && k.KeyHash == oldHash)
                    .ExecuteUpdateAsync(s => s.SetProperty(k => k.KeyHash, newHash), cancellationToken);
                return;
            }

            var entity = await db.IntegrationKeys.FirstOrDefaultAsync(k => k.Id == integrationKeyId, cancellationToken);
            if (entity is null || entity.KeyHash != oldHash)
                return;
            entity.KeyHash = newHash;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to upgrade key hash for integration key {IntegrationKeyId}", integrationKeyId);
        }
    }

    private async Task UpgradeLicenseKeyHashAsync(
        string licenseId,
        string lookupHash,
        string oldHash,
        string plainKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var newHash = KeyHasher.Hash(plainKey);
            if (db.Database.IsRelational())
            {
                await db.Licenses
                    .IgnoreQueryFilters()
                    .Where(l => l.Id == licenseId
                        && l.LicenseKeyLookupHash == lookupHash
                        && l.LicenseKeyHash == oldHash)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.LicenseKeyHash, newHash), cancellationToken);
                return;
            }

            var entity = await db.Licenses
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(l => l.Id == licenseId && l.LicenseKeyLookupHash == lookupHash, cancellationToken);
            if (entity is null || entity.LicenseKeyHash != oldHash)
                return;
            entity.LicenseKeyHash = newHash;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to upgrade key hash for license {LicenseId}", licenseId);
        }
    }

    private static ValidateLicenseResponse Invalid(
        string reason, string message, DateTime now, string? licenseId = null, int? maxDevices = null, int? activeDevices = null) => new()
    {
        IsValid = false,
        Reason = reason,
        Message = message,
        ServerTime = now,
        LicenseId = licenseId,
        MaxDevices = maxDevices,
        ActiveDevices = activeDevices
    };

    private sealed class Candidate
    {
        public required string Id { get; init; }
        public required string CustomerId { get; init; }
        public required string KeyHash { get; init; }
        public LicenseStatus Status { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public required string PlanName { get; init; }
        public int? MaxDevices { get; set; }
        public bool IsTrial { get; init; }
        public bool IsComplimentary { get; init; }
        public bool CustomerSuspended { get; init; }
        public DeviceRow? Device { get; init; }
        public int ActiveDevices { get; set; }
    }

    private sealed class DeviceRow
    {
        public required string Id { get; init; }
        public string? Name { get; init; }
        public DateTime LastSeenAt { get; init; }
        public DateTime? RevokedAt { get; init; }
    }
}
