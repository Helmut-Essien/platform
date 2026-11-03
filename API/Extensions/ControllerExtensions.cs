using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Platform.Api.Controllers;
using Platform.Shared.Dtos.Licenses;

namespace Platform.Api.Extensions;

public static class ControllerExtensions
{
    public static IServiceCollection AddPlatformControllers(this IServiceCollection services)
    {
        services.AddControllers()
            .ConfigureApiBehaviorOptions(options =>
            {
                options.InvalidModelStateResponseFactory = InvalidModelState;
            });

        return services;
    }

    internal static IActionResult InvalidModelState(ActionContext context)
    {
        var errors = context.ModelState
            .Where(e => e.Value?.Errors.Count > 0)
            .ToDictionary(
                e => e.Key,
                e => e.Value!.Errors
                    .Select(x => string.IsNullOrEmpty(x.ErrorMessage) ? "Validation failed." : x.ErrorMessage)
                    .ToArray());

        var message = errors.Values
            .SelectMany(v => v)
            .FirstOrDefault() ?? "Validation failed.";

        // Products calling the public license endpoints get the same machine-readable shape as every other answer.
        if (context.ActionDescriptor is ControllerActionDescriptor { ControllerTypeInfo: var type }
            && type.AsType() == typeof(LicenseValidationController))
        {
            return new BadRequestObjectResult(new
            {
                isValid = false,
                reason = LicenseValidationReasons.InvalidRequest,
                message,
                serverTime = DateTime.UtcNow,
                errors
            });
        }

        return new BadRequestObjectResult(new { message, errors });
    }
}
