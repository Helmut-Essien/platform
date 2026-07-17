using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Api.Services.Email;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class LicenseKeyDeliveryServiceTests
{
    [Fact]
    public async Task DeliverNewKeyAsync_QueuesEncryptedEmailAndStoresHashes()
    {
        await using var db = CreateDbContext();
        var customer = new Customer
        {
            Name = "Acme Ltd",
            ContactEmail = "owner@acme.test"
        };
        var serviceProduct = new ServiceProduct
        {
            Name = "Hostel Manager",
            Code = "HOSTEL"
        };
        var license = new License
        {
            Customer = customer,
            CustomerId = customer.Id,
            ServiceProduct = serviceProduct,
            ServiceProductId = serviceProduct.Id,
            Status = LicenseStatus.Pending,
            PlanName = "Growth"
        };

        db.AddRange(customer, serviceProduct, license);
        await db.SaveChangesAsync();

        var protector = CreateProtector();
        var service = new LicenseKeyDeliveryService(
            db, new EmailOutboxService(db), protector, new EmailTemplateService());

        await service.DeliverNewKeyAsync(license, isRenewal: false);
        await db.SaveChangesAsync();

        var message = Assert.Single(db.EmailOutboxMessages);
        Assert.Equal("owner@acme.test", message.ToEmail);
        Assert.Equal("Your Hostel Manager license is active", message.Subject);
        Assert.DoesNotContain("HOSTEL-", message.HtmlBody);
        Assert.Contains("Growth", message.HtmlBody);
        Assert.StartsWith("HOSTEL-", protector.Unprotect(message.EncryptedPayload!));

        Assert.NotNull(license.LicenseKeyHash);
        Assert.NotNull(license.LicenseKeyLookupHash);
        Assert.NotNull(license.LicenseKeySentAt);
        Assert.DoesNotContain("HOSTEL-", license.LicenseKeyHash);
        Assert.DoesNotContain("HOSTEL-", license.LicenseKeyLookupHash);
    }

    [Fact]
    public async Task DeliverNewKeyAsync_RenewalUsesRenewedSubject()
    {
        await using var db = CreateDbContext();
        var customer = new Customer
        {
            Name = "Acme Ltd",
            ContactEmail = "owner@acme.test"
        };
        var serviceProduct = new ServiceProduct
        {
            Name = "Laundry Manager",
            Code = "LAUNDRY"
        };
        var license = new License
        {
            Customer = customer,
            CustomerId = customer.Id,
            ServiceProduct = serviceProduct,
            ServiceProductId = serviceProduct.Id,
            Status = LicenseStatus.Active,
            PlanName = "Pro"
        };

        db.AddRange(customer, serviceProduct, license);
        await db.SaveChangesAsync();

        var service = new LicenseKeyDeliveryService(
            db, new EmailOutboxService(db), CreateProtector(), new EmailTemplateService());

        await service.DeliverNewKeyAsync(license, isRenewal: true);
        await db.SaveChangesAsync();

        var message = Assert.Single(db.EmailOutboxMessages);
        Assert.Equal("Your Laundry Manager license key has been rotated", message.Subject);
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    private static EmailPayloadProtector CreateProtector()
    {
        var settings = Options.Create(new EmailSettings
        {
            Outbox = new EmailOutboxSettings
            {
                EncryptionKey = Convert.ToBase64String(new byte[32])
            }
        });
        return new EmailPayloadProtector(settings, new ConfigurationBuilder().Build());
    }
}
