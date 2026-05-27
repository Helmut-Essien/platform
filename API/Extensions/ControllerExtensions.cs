using Microsoft.AspNetCore.Mvc;

namespace Platform.Api.Extensions;

public static class ControllerExtensions
{
    public static IServiceCollection AddPlatformControllers(this IServiceCollection services)
    {
        services.AddControllers()
            .ConfigureApiBehaviorOptions(options =>
            {
                options.InvalidModelStateResponseFactory = context =>
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

                    return new BadRequestObjectResult(new { message, errors });
                };
            });

        return services;
    }
}
