namespace Platform.Client.Services;

/// <summary>Result of fetching a single resource: found, not found (404), or the request failed.</summary>
public sealed record LookupResult<T>(T? Value, bool NotFound) where T : class
{
    public bool IsFound => Value is not null;
    public bool IsError => Value is null && !NotFound;

    public static LookupResult<T> Found(T value) => new(value, false);
    public static LookupResult<T> Missing() => new(null, true);
    public static LookupResult<T> Failed() => new(null, false);
}
