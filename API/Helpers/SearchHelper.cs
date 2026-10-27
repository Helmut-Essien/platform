namespace Platform.Api.Helpers;

public static class SearchHelper
{
    /// <summary>
    /// Escapes LIKE/ILIKE wildcards (<c>\</c>, <c>%</c>, <c>_</c>) so user input is matched literally
    /// (PostgreSQL's default LIKE escape character is a backslash).
    /// </summary>
    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Builds a "contains" ILIKE pattern from raw user input.</summary>
    public static string ContainsPattern(string value) => $"%{EscapeLike(value.Trim())}%";
}
