using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Security;

namespace Platform.Api.Services.Licensing;

/// <summary>
/// Limits on top of the per-IP+integration-key middleware policy: per license key (all IPs) and per IP across
/// integration keys. Partition keys are hashes, never raw keys or addresses.
/// </summary>
public sealed class LicenseValidationRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string>? _perLicense;
    private readonly PartitionedRateLimiter<string>? _perIp;

    public LicenseValidationRateLimiter(IOptions<LicensingSettings> options)
    {
        var s = options.Value;
        var window = TimeSpan.FromSeconds(Math.Clamp(s.RateLimitWindowSeconds, 1, 3600));
        _perLicense = Create(s.LicenseKeyPermitLimit, window);
        _perIp = Create(s.PerIpPermitLimit, window);
    }

    private static PartitionedRateLimiter<string>? Create(int limit, TimeSpan window) =>
        limit <= 0
            ? null
            : PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ =>
                new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = window, QueueLimit = 0, AutoReplenishment = true }));

    /// <summary>Returns null when allowed, otherwise the time to wait.</summary>
    public TimeSpan? TryAcquire(string? clientIp, string? licenseKey)
    {
        if (_perIp is not null && Rejected(_perIp, "ip:" + KeyLookupHasher.ComputeSha256Hex(clientIp ?? "unknown")[..32]) is { } ipWait)
            return ipWait;
        if (_perLicense is not null && !string.IsNullOrEmpty(licenseKey)
            && Rejected(_perLicense, "lk:" + KeyLookupHasher.ComputeSha256Hex(licenseKey)[..32]) is { } keyWait)
            return keyWait;
        return null;
    }

    private static TimeSpan? Rejected(PartitionedRateLimiter<string> limiter, string key)
    {
        using var lease = limiter.AttemptAcquire(key);
        if (lease.IsAcquired)
            return null;
        return lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) && retry > TimeSpan.Zero
            ? retry
            : TimeSpan.FromSeconds(1);
    }

    public void Dispose()
    {
        _perLicense?.Dispose();
        _perIp?.Dispose();
    }
}
