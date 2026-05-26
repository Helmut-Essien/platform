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
                    var message = context.ModelState.Values
                        .SelectMany(v => v.Errors)
                        .Select(e => string.IsNullOrEmpty(e.ErrorMessage) ? "Validation failed." : e.ErrorMessage)
                        .First();
                    return new BadRequestObjectResult(new { message });
                };
            });

        return services;
    }
}
