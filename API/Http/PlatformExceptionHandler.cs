using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Conflict"),
            InvalidOperationException => (StatusCodes.Status400BadRequest, "Bad Request"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Bad Request"),
            _ => (StatusCodes.Status500InternalServerError, "Server Error")
        };

        var detail = exception is DbUpdateConcurrencyException
            ? "The record was updated by someone else. Refresh and try again."
            : exception.Message;

        if (status >= StatusCodes.Status500InternalServerError)
            logger.LogError(exception, "Unhandled exception");
        else
            logger.LogInformation(exception, "Request failed with {StatusCode}: {Message}", status, detail);

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
                message = detail,
                title,
                detail,
                status
            },
            cancellationToken);
        return true;
    }
}
