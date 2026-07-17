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

        Assert.Equal("Invoice INV-2026-00001 from Platform License Hub", template.Subject);
        Assert.Contains("Platform License Hub", template.Html);
        Assert.Contains("This is an automated transactional message", template.Html);
        Assert.Contains("$100.00", template.Html);
        Assert.Contains("$20.00", template.Html);
        Assert.Contains("$120.00", template.Html);
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

        Assert.Contains("$120.00", template.Html);
        Assert.DoesNotContain("120.00 USD", template.Html);
        Assert.Contains("July 17, 2026", template.Html);
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
        Assert.Contains("Reason:", template.Html);
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
