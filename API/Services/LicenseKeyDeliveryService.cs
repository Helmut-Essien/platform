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
    EmailTemplateService templates,
    ILicenseDenyListService? denyList = null) : ILicenseKeyDeliveryService
{
    public async Task DeliverNewKeyAsync(License license, bool isRenewal, CancellationToken cancellationToken = default)
    {
        if (denyList is not null && !string.IsNullOrEmpty(license.LicenseKeyLookupHash))
        {
            await denyList.InvalidateValidationCacheAsync(
                license.ServiceProductId,
                license.LicenseKeyLookupHash,
                cancellationToken);
        }

        var serviceProduct = license.ServiceProduct
            ?? await db.ServiceProducts.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == license.ServiceProductId, cancellationToken)
            ?? throw new NotFoundException("Service product not found.");

        var customer = license.Customer
            ?? await db.Customers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == license.CustomerId, cancellationToken)
            ?? throw new NotFoundException("Customer not found.");

        var plainKey = GenerateLicenseKey(serviceProduct.Code);
        license.LicenseKeyHash = KeyHasher.Hash(plainKey);
        license.LicenseKeyLookupHash = KeyLookupHasher.ComputeSha256Hex(plainKey);
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
        // 4 x 4 chars from a 32-symbol alphabet = 80 bits of entropy (was 2 x 4 = 40 bits).
        // Validation is by hash lookup, so previously issued shorter keys keep working.
        return $"{code}-{RandomSegment(4)}-{RandomSegment(4)}-{RandomSegment(4)}-{RandomSegment(4)}";
    }

    private static string RandomSegment(int length)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++)
            sb.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
        return sb.ToString();
    }
}
