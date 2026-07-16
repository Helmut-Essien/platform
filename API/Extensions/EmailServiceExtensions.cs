using Platform.Api.Configuration;
using Platform.Api.Services;
using Platform.Api.Services.Email;

namespace Platform.Api.Extensions;

public static class EmailServiceExtensions
{
    public static IServiceCollection AddPlatformEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmailSettings>(configuration.GetSection(EmailSettings.SectionName));

        var emailSection = configuration.GetSection(EmailSettings.SectionName);
        var provider = emailSection.GetValue<string>("Provider") ?? "Logging";
        var configuredTimeout = emailSection.GetValue<int?>("TimeoutSeconds");
        var timeoutSeconds = configuredTimeout is > 0 ? configuredTimeout.Value : 30;

        if (string.Equals(provider, "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else if (string.Equals(provider, "Resend", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient(ResendEmailSender.HttpClientName, client =>
            {
                client.BaseAddress = new Uri("https://api.resend.com/");
                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            });
            services.AddSingleton<IEmailSender, ResendEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, LoggingEmailSender>();
        }

        services.AddScoped<ILicenseKeyDeliveryService, LicenseKeyDeliveryService>();

        return services;
    }
}
