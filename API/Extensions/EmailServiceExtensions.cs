using Platform.Api.Configuration;
using Platform.Api.Services;
using Platform.Api.Services.Email;

namespace Platform.Api.Extensions;

public static class EmailServiceExtensions
{
    public static IServiceCollection AddPlatformEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmailSettings>(configuration.GetSection(EmailSettings.SectionName));

        var provider = configuration.GetSection(EmailSettings.SectionName).GetValue<string>("Provider") ?? "Logging";

        if (string.Equals(provider, "Smtp", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        else
            services.AddSingleton<IEmailSender, LoggingEmailSender>();

        services.AddScoped<ILicenseKeyDeliveryService, LicenseKeyDeliveryService>();

        return services;
    }
}
