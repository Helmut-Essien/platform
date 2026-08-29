using System.Net;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Identity;
using Platform.Api.Services.Email;

namespace Platform.Api.Services;

public class AdminAuthService(
    UserManager<ApplicationUser> userManager,
    IEmailSender emailSender,
    EmailTemplateService templates,
    IOptions<AuthSettings> authSettings,
    ILogger<AdminAuthService> logger) : IAdminAuthService
{
    public async Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = email.Trim();
        var user = await userManager.FindByEmailAsync(normalizedEmail);
        if (user is null)
        {
            logger.LogDebug("Password reset requested for unknown email {Email}", normalizedEmail);
            return;
        }

        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var baseUrl = authSettings.Value.ClientBaseUrl.TrimEnd('/');
        var resetLink =
            $"{baseUrl}/reset-password?email={Uri.EscapeDataString(normalizedEmail)}&token={encodedToken}";

        var template = templates.PasswordReset(normalizedEmail, resetLink);
        await emailSender.SendAsync(normalizedEmail, template.Subject, template.Html, cancellationToken: cancellationToken);
        logger.LogInformation("Password reset email sent to {Email}", normalizedEmail);
    }

    public async Task ResetPasswordAsync(
        string email,
        string token,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByEmailAsync(email.Trim());
        if (user is null)
            throw new InvalidOperationException("Invalid reset request.");

        string decodedToken;
        try
        {
            decodedToken = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(token));
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Invalid reset token.");
        }

        var result = await userManager.ResetPasswordAsync(user, decodedToken, newPassword);
        if (!result.Succeeded)
        {
            var message = string.Join(" ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                ? "Password reset failed."
                : message);
        }
    }
}
