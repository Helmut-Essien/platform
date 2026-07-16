using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task DeliverNewKeyAsync_SendsLicenseKeyEmailAndStoresHashes()
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

        var emailSender = new CapturingEmailSender();
        var service = new LicenseKeyDeliveryService(
            db,
            emailSender,
            NullLogger<LicenseKeyDeliveryService>.Instance);

        await service.DeliverNewKeyAsync(license, isRenewal: false);

        Assert.Single(emailSender.Messages);
        var message = emailSender.Messages[0];
        Assert.Equal("owner@acme.test", message.ToEmail);
        Assert.Equal("Your Hostel Manager license is active", message.Subject);
        Assert.Contains("HOSTEL-", message.HtmlBody);
        Assert.Contains("Growth", message.HtmlBody);

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

        var emailSender = new CapturingEmailSender();
        var service = new LicenseKeyDeliveryService(
            db,
            emailSender,
            NullLogger<LicenseKeyDeliveryService>.Instance);

        await service.DeliverNewKeyAsync(license, isRenewal: true);

        Assert.Single(emailSender.Messages);
        Assert.Equal("Your Laundry Manager license has been renewed", emailSender.Messages[0].Subject);
    }

    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(NUlid.Ulid.NewUlid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    private sealed class CapturingEmailSender : IEmailSender
    {
        public List<EmailMessage> Messages { get; } = [];

        public Task SendAsync(
            string toEmail,
            string subject,
            string htmlBody,
            IReadOnlyList<EmailAttachment>? attachments = null,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(new EmailMessage(toEmail, subject, htmlBody));
            return Task.CompletedTask;
        }
    }

    private sealed record EmailMessage(string ToEmail, string Subject, string HtmlBody);
}
