namespace Platform.Api.Helpers;

public static class SearchHelper
{
    /// <summary>
    /// Escapes LIKE/ILIKE wildcards (<c>\</c>, <c>%</c>, <c>_</c>) so user input is matched literally
    /// (PostgreSQL's default LIKE escape character is a backslash).
    /// </summary>
    /// <summary>Longest search term the list endpoints evaluate; longer input is cut (it cannot match more).</summary>
    public const int MaxSearchLength = 100;

    /// <summary>Trims user input and caps its length so an oversized term cannot be turned into a heavy scan.</summary>
    public static string Normalize(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxSearchLength ? trimmed : trimmed[..MaxSearchLength].TrimEnd();
    }

    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Builds a "contains" ILIKE pattern from raw user input.</summary>
    public static string ContainsPattern(string value) => $"%{EscapeLike(Normalize(value))}%";
}
