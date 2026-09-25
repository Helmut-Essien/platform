using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Services;

namespace Platform.Api.Http;

public sealed class PlatformExceptionHandler(ILogger<PlatformExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Not Found"),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
            InvalidOperationException => (StatusCodes.Status400BadRequest, "Bad Request"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Bad Request"),
            _ => (StatusCodes.Status500InternalServerError, "Server Error")
        };

        if (status >= StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Unhandled exception");
        else
            logger.LogInformation(exception, "Request failed with {StatusCode}: {Message}", status, exception.Message);

        if (status >= StatusCodes.Status500InternalServerError)
        {
            var serverProblem = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = "An unexpected error occurred.",
                Instance = httpContext.Request.Path
            };
            // Keep legacy client shape alongside Problem Details.
            httpContext.Response.StatusCode = status;
            await httpContext.Response.WriteAsJsonAsync(
                new { message = serverProblem.Detail, title, detail = serverProblem.Detail, status },
                cancellationToken);
            return true;
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(
            new
            {
                message = exception.Message,
                title,
                detail = exception.Message,
                status
            },
            cancellationToken);
        return true;
    }
}
