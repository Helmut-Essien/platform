using System.Security.Claims;

namespace Platform.Api.Http;

public static class AdminRequestContext
{
    public static string GetPerformedBy(HttpContext httpContext)
    {
        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            return httpContext.User.FindFirstValue(ClaimTypes.Name)
                ?? httpContext.User.FindFirstValue(ClaimTypes.Email)
                ?? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? "admin";
        }

        return "system";
    }

    public static string? GetIpAddress(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString();
}
