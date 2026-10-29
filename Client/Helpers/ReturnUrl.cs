namespace Platform.Client.Helpers;

/// <summary>Validates post-login return URLs so only local, non-auth app paths are accepted (no open redirects).</summary>
public static class ReturnUrl
{
    private static readonly string[] AuthPaths = ["/login", "/forgot-password", "/reset-password"];

    /// <summary>Returns a safe local path ("/..."), or "/" when <paramref name="value"/> is missing or unsafe.</summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "/";

        var url = value.Trim();

        // Must be a rooted local path; reject protocol-relative (//host), backslash tricks (/\host),
        // control characters and anything scheme-like in the path part.
        if (url[0] != '/' || url.Contains('\\') || url.Any(char.IsControl))
            return "/";
        if (url.Length > 1 && url[1] == '/')
            return "/";

        var path = url.Split('?', '#')[0];
        if (path.Contains(':'))
            return "/";

        path = path.TrimEnd('/');
        if (AuthPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)))
            return "/";

        return url;
    }

    /// <summary>Builds the login URL carrying the current location as a return URL (omitted for "/" or auth pages).</summary>
    public static string LoginUrlFor(string relativePath)
    {
        var target = Sanitize("/" + (relativePath ?? "").TrimStart('/'));
        return target == "/" ? "login" : $"login?returnUrl={Uri.EscapeDataString(target)}";
    }
}
