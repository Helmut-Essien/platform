using System.Security.Claims;

namespace Platform.Api.Http;

public static class AdminRequestContext
{
    public const string PerformedByHeader = "X-Admin-User";

    public static string GetPerformedBy(HttpContext httpContext)
    {
        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            return httpContext.User.FindFirstValue(ClaimTypes.Name)
                ?? httpContext.User.FindFirstValue(ClaimTypes.Email)
                ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? "admin";
        }

        return httpContext.Request.Headers[PerformedByHeader].FirstOrDefault() ?? "admin";
    }

    public static string? GetIpAddress(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString();
}
