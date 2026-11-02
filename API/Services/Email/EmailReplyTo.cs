using System.Net.Mail;

namespace Platform.Api.Services.Email;

/// <summary>
/// Validates Email:ReplyToAddress for every provider. An invalid value is ignored (the email goes without a
/// Reply-To) with a warning, instead of failing delivery (Resend rejects the whole request) or being dropped silently.
/// </summary>
public static class EmailReplyTo
{
    /// <summary>The trimmed bare address, or null when unset or invalid (logs a warning when invalid).</summary>
    public static string? Resolve(string? configured, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return null;
        var value = configured.Trim();
        if (IsValid(value))
            return value;
        logger.LogWarning(
            "Email:ReplyToAddress '{ReplyTo}' is not a valid email address; sending without a Reply-To header.", value);
        return null;
    }

    /// <summary>A single bare address (no display name, no list).</summary>
    public static bool IsValid(string value)
    {
        if (value.Contains(',') || value.Contains(';') || value.Contains('<'))
            return false;
        try
        {
            var parsed = new MailAddress(value);
            return string.Equals(parsed.Address, value, StringComparison.OrdinalIgnoreCase)
                && parsed.Host.Contains('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
