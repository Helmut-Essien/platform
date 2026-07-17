using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Security;
using Platform.Api.Services.Email;
using Platform.Shared.Enums;

namespace Platform.Api.Services;

public class LicenseKeyDeliveryService(
    AppDbContext db,
    IEmailOutboxService outbox,
    EmailPayloadProtector protector,
    EmailTemplateService templates) : ILicenseKeyDeliveryService
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

        var template = templates.LicenseKey(customer, serviceProduct, license, "{{LICENSE_KEY}}", isRenewal);
        outbox.Enqueue(
            isRenewal ? EmailDeliveryKind.LicenseKeyRotated : EmailDeliveryKind.LicenseKey,
            CustomerContactResolver.Technical(customer),
            template.Subject,
            template.Html,
            customer.Id,
            license.Id,
            encryptedPayload: protector.Protect(plainKey));

        // Keep the request asynchronous without performing network I/O.
        await Task.CompletedTask;
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

}
