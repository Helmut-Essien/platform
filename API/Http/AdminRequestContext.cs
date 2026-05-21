namespace Platform.Api.Http;

public static class AdminRequestContext
{
    public const string PerformedByHeader = "X-Admin-User";

    public static string GetPerformedBy(HttpContext httpContext) =>
        httpContext.Request.Headers[PerformedByHeader].FirstOrDefault() ?? "admin";

    public static string? GetIpAddress(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString();
}
