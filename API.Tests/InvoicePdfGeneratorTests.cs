using System.Text;
using Platform.Api.Entities;
using Platform.Api.Services.Billing;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class InvoicePdfGeneratorTests
{
    [Fact]
    public void Generate_ReturnsValidPdfBytes()
    {
        var generator = new InvoicePdfGenerator();
        var customer = new Customer
        {
            Name = "Acme Ltd",
            ContactEmail = "billing@acme.test",
            ContactPhone = "+233 00 000 0000"
        };
        var invoice = new Invoice
        {
            CustomerId = customer.Id,
            InvoiceNumber = "INV-2026-00042",
            Status = InvoiceStatus.Sent,
            IssueDate = new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc),
            Currency = "USD",
            Subtotal = 100m,
            TaxAmount = 12.5m,
            TotalAmount = 112.5m,
            PlanName = "Growth",
            Description = "Asset Management license",
            ServiceProduct = new ServiceProduct
            {
                Name = "Asset Management",
                Code = "ASSET"
            }
        };

        var letterhead = new InvoiceLetterhead(
            "HelmutCode",
            "Accra, Ghana",
            null,
            "+233 00 000 0000",
            "https://helmutcode.com",
            LogoBytes: null,
            PaymentOptions:
            [
                new InvoicePaymentOption("Bank transfer", "Account: 1234567890\nReference: invoice number"),
                new InvoicePaymentOption("MTN MoMo", "024 000 0000")
            ]);

        var pdf = generator.Generate(invoice, customer, letterhead);

        Assert.NotEmpty(pdf);
        Assert.True(pdf.Length > 100);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }

    [Fact]
    public void Generate_UsesCustomLogoWhenProvided()
    {
        var generator = new InvoicePdfGenerator();
        var customer = new Customer { Name = "Acme", ContactEmail = "a@test.com" };
        var invoice = new Invoice
        {
            CustomerId = customer.Id,
            InvoiceNumber = "INV-2026-00001",
            Status = InvoiceStatus.Sent,
            Currency = "USD",
            Subtotal = 10m,
            TaxAmount = 0m,
            TotalAmount = 10m
        };

        var tinyPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var letterhead = new InvoiceLetterhead("Brand Co", null, null, null, null, tinyPng, []);

        var pdf = generator.Generate(invoice, customer, letterhead);

        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }
}
