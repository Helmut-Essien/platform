using Platform.Api.Configuration;
using Platform.Api.Services;
using Platform.Api.Services.Billing;
using Platform.Api.Services.Email;

namespace Platform.Api.Extensions;

public static class EmailServiceExtensions
{
    public static IServiceCollection AddPlatformEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmailSettings>(configuration.GetSection(EmailSettings.SectionName));

        var emailSection = configuration.GetSection(EmailSettings.SectionName);
        var provider = emailSection.GetValue<string>("Provider")?.Trim() ?? "Logging";
        var configuredTimeout = emailSection.GetValue<int?>("TimeoutSeconds");
        var timeoutSeconds = configuredTimeout is > 0 ? configuredTimeout.Value : 30;
        var hasResendKey = !string.IsNullOrWhiteSpace(emailSection.GetValue<string>("ResendApiKey"));
        var hasSmtpHost = !string.IsNullOrWhiteSpace(emailSection.GetValue<string>("Host"));

        string selected;
        if (string.Equals(provider, "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
            selected = nameof(SmtpEmailSender);
        }
        else if (string.Equals(provider, "Resend", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient(ResendEmailSender.HttpClientName, client =>
            {
                client.BaseAddress = new Uri("https://api.resend.com/");
                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            });
            services.AddSingleton<IEmailSender, ResendEmailSender>();
            selected = nameof(ResendEmailSender);
        }
        else
        {
            services.AddSingleton<IEmailSender, LoggingEmailSender>();
            selected = nameof(LoggingEmailSender);
        }

        Console.Error.WriteLine(
            $"[Startup] Email provider config: Provider='{provider}' → {selected} " +
            $"(ResendApiKey set={hasResendKey}, Smtp Host set={hasSmtpHost})");

        services.AddSingleton<IInvoicePdfGenerator, InvoicePdfGenerator>();
        services.AddScoped<ILicenseKeyDeliveryService, LicenseKeyDeliveryService>();

        return services;
    }
}
