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
            Platform.Api.Services.Payments.OnlinePaymentsDisabledException => (StatusCodes.Status503ServiceUnavailable, "Service Unavailable"),
            Platform.Api.Services.Payments.PaymentGatewayException => (StatusCodes.Status502BadGateway, "Bad Gateway"),
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

        var isPaymentProblem = status is StatusCodes.Status502BadGateway or StatusCodes.Status503ServiceUnavailable;
        if (status >= StatusCodes.Status500InternalServerError && !isPaymentProblem)
            logger.LogError(exception, "Unhandled exception");
        else
            logger.LogInformation(exception, "Request failed with {StatusCode}: {Message}", status, detail);

        if (status >= StatusCodes.Status500InternalServerError && !isPaymentProblem)
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
