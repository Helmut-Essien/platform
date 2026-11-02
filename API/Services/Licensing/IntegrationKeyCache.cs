using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;

namespace Platform.Api.Services.Licensing;

/// <summary>
/// In-process cache of successfully resolved integration keys, so a license check costs one database round trip.
/// Only hits are cached (random keys from attackers cannot grow it). Any key change clears it (generation bump).
/// </summary>
public sealed class IntegrationKeyCache(IOptions<LicensingSettings> settings, TimeProvider? clock = null)
{
    public sealed class Entry(string integrationKeyId, string serviceProductId, string serviceProductCode, DateTime? lastUsedAt)
    {
        public string IntegrationKeyId { get; } = integrationKeyId;
        public string ServiceProductId { get; } = serviceProductId;
        public string ServiceProductCode { get; } = serviceProductCode;
        /// <summary>Mutable so a LastUsed write is not repeated on every cached hit.</summary>
        public DateTime? LastUsedAt { get; set; } = lastUsedAt;
    }

    private sealed record Slot(Entry Value, DateTime ExpiresAt, long Generation);

    private const int MaxEntries = 1000;
    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _generation;

    private TimeSpan Ttl => TimeSpan.FromSeconds(Math.Clamp(settings.Value.IntegrationKeyCacheSeconds, 0, 3600));

    public static string KeyOf(string lookupHash, string? serviceCode) =>
        string.IsNullOrWhiteSpace(serviceCode) ? lookupHash : lookupHash + "|" + serviceCode.Trim().ToUpperInvariant();

    /// <summary>Generation to pass to <see cref="Set"/>: read it before the database lookup so a concurrent
    /// invalidation can never be overwritten by a stale result.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    public bool TryGet(string key, out Entry entry)
    {
        if (_slots.TryGetValue(key, out var slot)
            && slot.Generation == Generation
            && slot.ExpiresAt > _clock.GetUtcNow().UtcDateTime)
        {
            entry = slot.Value;
            return true;
        }
        entry = null!;
        return false;
    }

    public void Set(string key, Entry entry, long generation)
    {
        var ttl = Ttl;
        if (ttl <= TimeSpan.Zero || generation != Generation)
            return;
        if (_slots.Count >= MaxEntries)
            _slots.Clear();
        _slots[key] = new Slot(entry, _clock.GetUtcNow().UtcDateTime + ttl, generation);
    }

    /// <summary>Call after any integration key is created, rotated or revoked.</summary>
    public void InvalidateAll()
    {
        Interlocked.Increment(ref _generation);
        _slots.Clear();
    }
}
