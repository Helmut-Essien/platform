using System.Collections.Concurrent;

namespace Platform.Api.Services.Licensing;

/// <summary>
/// Remembers when an email mismatch was last audited per license so a flood of wrong guesses writes at most one
/// audit row per license per window. In memory (single instance), bounded: a full map is pruned of old entries.
/// </summary>
public sealed class LicenseEmailAuditThrottle
{
    internal const int MaxEntries = 10_000;
    private readonly ConcurrentDictionary<string, DateTime> _last = new(StringComparer.Ordinal);

    /// <summary>True when an audit row should be written now for this license (and records it).</summary>
    public bool TryEnter(string licenseId, DateTime now, TimeSpan window)
    {
        while (true)
        {
            if (_last.TryGetValue(licenseId, out var previous))
            {
                if (now - previous < window)
                    return false;
                if (_last.TryUpdate(licenseId, now, previous))
                    return true;
                continue;
            }

            if (_last.Count >= MaxEntries)
                Prune(now, window);
            if (_last.TryAdd(licenseId, now))
                return true;
        }
    }

    internal int Count => _last.Count;

    private void Prune(DateTime now, TimeSpan window)
    {
        foreach (var (key, at) in _last)
        {
            if (now - at >= window)
                _last.TryRemove(new KeyValuePair<string, DateTime>(key, at));
        }
        // Everything is recent (a broad attack): drop the lot rather than grow without bound.
        if (_last.Count >= MaxEntries)
            _last.Clear();
    }
}
