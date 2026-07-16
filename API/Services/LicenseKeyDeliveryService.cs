using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Security;
using Platform.Api.Services.Email;

namespace Platform.Api.Services;

public class LicenseKeyDeliveryService(
    AppDbContext db,
    IEmailSender emailSender,
    ILogger<LicenseKeyDeliveryService> logger) : ILicenseKeyDeliveryService
{
    public async Task DeliverNewKeyAsync(License license, bool isRenewal, CancellationToken cancellationToken = default)
    {
        var serviceProduct = license.ServiceProduct
            ?? await db.ServiceProducts.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == license.ServiceProductId, cancellationToken)
            ?? throw new InvalidOperationException("Service product not found.");

        var customer = license.Customer
            ?? await db.Customers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == license.CustomerId, cancellationToken)
            ?? throw new InvalidOperationException("Customer not found.");

        var plainKey = GenerateLicenseKey(serviceProduct.Code);
        license.LicenseKeyHash = BCrypt.Net.BCrypt.HashPassword(plainKey);
        license.LicenseKeyLookupHash = KeyLookupHasher.ComputeSha256Hex(plainKey);
        license.LicenseKeySentAt = DateTime.UtcNow;
        license.UpdatedAt = DateTime.UtcNow;

        var subject = isRenewal
            ? $"Your {serviceProduct.Name} license has been renewed"
            : $"Your {serviceProduct.Name} license is active";

        var htmlBody = BuildEmailBody(customer.Name, serviceProduct.Name, license.PlanName, plainKey, isRenewal);

        try
        {
            await emailSender.SendAsync(customer.ContactEmail, subject, htmlBody, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            var wasCanceled = ex is OperationCanceledException;
            logger.LogError(
                ex,
                "Failed to send license key email for license {LicenseId} to customer {CustomerId} ({Recipient}). " +
                "IsRenewal={IsRenewal}. Canceled={WasCanceled}. CancellationRequested={CancellationRequested}. ExceptionType={ExceptionType}",
                license.Id,
                license.CustomerId,
                customer.ContactEmail,
                isRenewal,
                wasCanceled,
                cancellationToken.IsCancellationRequested,
                ex.GetType().FullName);
            throw new InvalidOperationException("License was updated but the license key email could not be sent.", ex);
        }
    }

    internal static string GenerateLicenseKey(string serviceCode)
    {
        var code = serviceCode.Trim().ToUpperInvariant();
        var segment1 = RandomSegment(4);
        var segment2 = RandomSegment(4);
        return $"{code}-{segment1}-{segment2}";
    }

    private static string RandomSegment(int length)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            sb.Append(chars[bytes[i] % chars.Length]);
        return sb.ToString();
    }

    private static string BuildEmailBody(
        string customerName,
        string productName,
        string planName,
        string licenseKey,
        bool isRenewal)
    {
        var action = isRenewal ? "renewed" : "activated";
        return $"""
            <html><body style="font-family:sans-serif;">
            <p>Hello {System.Net.WebUtility.HtmlEncode(customerName)},</p>
            <p>Your <strong>{System.Net.WebUtility.HtmlEncode(productName)}</strong> license ({System.Net.WebUtility.HtmlEncode(planName)}) has been {action}.</p>
            <p>Your license key:</p>
            <p style="font-family:monospace;font-size:1.1em;"><strong>{System.Net.WebUtility.HtmlEncode(licenseKey)}</strong></p>
            <p>Store this key securely. It will not be sent again.</p>
            </body></html>
            """;
    }
}
