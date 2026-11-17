using Platform.Api.Entities;
using Platform.Api.Services.Email;
using Platform.Shared.Enums;
using Xunit;

namespace Platform.Api.Tests;

public class EmailTemplateServiceTests
{
    private readonly EmailTemplateService templates = new();

    [Fact]
    public void Invoice_UsesSharedBrandedShellAndConsistentCurrencyFormatting()
    {
        var customer = CreateCustomer();
        var invoice = CreateInvoice();

        var template = templates.Invoice(customer, invoice);

        Assert.Equal("Invoice INV-2026-00001 from HelmutCode Solutions", template.Subject);
        Assert.Contains("HelmutCode Solutions", template.Html);
        Assert.Contains("Kind regards", template.Html);
        Assert.Contains("USD 100.00", template.Html);
        Assert.Contains("USD 20.00", template.Html);
        Assert.Contains("USD 120.00", template.Html);
        Assert.Contains("invoice PDF is attached", template.Html);
    }

    [Fact]
    public void PaymentReceipt_UsesSameCurrencyFormatAsInvoice()
    {
        var customer = CreateCustomer();
        var invoice = CreateInvoice();
        var receipt = new Receipt
        {
            InvoiceId = invoice.Id,
            ReceiptNumber = "RCP-2026-00001",
            AmountPaid = 120m,
            PaidAt = new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc)
        };

        var template = templates.PaymentReceipt(customer, invoice, receipt);

        Assert.Contains("USD 120.00", template.Html);
        Assert.DoesNotContain("120.00 USD", template.Html);
        Assert.DoesNotContain("$120.00", template.Html);
        Assert.Contains("17 July 2026", template.Html);
    }

    [Theory]
    [InlineData(1200, "GHS", "GHS 1,200.00")]
    [InlineData(1200, " ghs ", "GHS 1,200.00")]
    [InlineData(0.5, "GHS", "GHS 0.50")]
    [InlineData(1234567.891, "GHS", "GHS 1,234,567.89")]
    [InlineData(50, "USD", "USD 50.00")]
    [InlineData(50, "", "GHS 50.00")]
    [InlineData(50, null, "GHS 50.00")]
    public void Money_IsTheCodeThenTheAmount_InEveryCulture(double amount, string? currency, string expected)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // A comma-decimal culture must not change emails or PDFs.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal(expected, Platform.Shared.Formatting.MoneyText.Format((decimal)amount, currency));
            Assert.Equal(expected, EmailTemplateService.Money((decimal)amount, currency!));
            Assert.Equal(expected, Platform.Api.Services.Billing.InvoicePdfGenerator.Money((decimal)amount, currency!));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Money_EncodesAStrangeStoredCurrency()
    {
        Assert.Equal("&lt;B&gt; 1.00", EmailTemplateService.Money(1m, "<b>"));
    }

    [Fact]
    public void StatusNotice_NormalizesAndEncodesAdminReason()
    {
        var customer = CreateCustomer();

        var template = templates.StatusNotice(
            customer,
            null,
            EmailDeliveryKind.Suspended,
            "Payment overdue <script>alert('x')</script>");

        Assert.Contains(
            "Payment overdue &lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;.",
            template.Html);
        Assert.DoesNotContain("<script>", template.Html);
        Assert.Contains("Why:", template.Html);
    }

    private static Customer CreateCustomer() =>
        new()
        {
            Name = "Acme Ltd",
            ContactEmail = "admin@acme.test"
        };

    private static Invoice CreateInvoice() =>
        new()
        {
            CustomerId = "customer-1",
            InvoiceNumber = "INV-2026-00001",
            Currency = "USD",
            IssueDate = new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 7, 31, 0, 0, 0, DateTimeKind.Utc),
            PlanName = "Pro",
            Description = "July license",
            Subtotal = 100m,
            TaxAmount = 20m,
            TotalAmount = 120m
        };
}
