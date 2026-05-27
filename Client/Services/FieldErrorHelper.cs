namespace Platform.Client.Services;

public static class FieldErrorHelper
{
    public static string? GetFieldError(
        IReadOnlyDictionary<string, string[]>? errors,
        params string[] keys)
    {
        if (errors is null)
            return null;

        foreach (var key in keys)
        {
            if (errors.TryGetValue(key, out var messages) && messages.Length > 0)
                return messages[0];
        }

        return null;
    }

    public static bool HasFieldErrors(IReadOnlyDictionary<string, string[]>? errors) =>
        errors is { Count: > 0 };
}
