using System.Text;
using Platform.Api.Entities;
using Platform.Api.Services.Billing;
using Platform.Api.Services.Email;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class EmailAndPdfPolishTests
{
    private readonly EmailTemplateService templates = new("https://licenses.example.com");
    private static readonly DateTime Now = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Invoice_HidesTaxRowWhenZero_AndPutsPayButtonAboveTable()
    {
        var invoice = Invoice(tax: 0m);
        var (subject, html) = templates.Invoice(Customer(), invoice, "https://pay.example/x");

        Assert.Contains("INV-2026-00007", subject);
        Assert.DoesNotContain(">Tax<", html);
        Assert.Contains("Pay GH₵150.00 now", html);
        Assert.Contains("MTN MoMo, Telecel Cash or card", html);
        Assert.True(html.IndexOf("Pay GH₵150.00 now", StringComparison.Ordinal)
            < html.IndexOf("INV-2026-00007</td>", StringComparison.Ordinal) || !html.Contains("INV-2026-00007</td>"));
        Assert.Contains("2 December 2026", html);
        Assert.Contains("#3f7a12", html);
        Assert.Contains("alt=\"HelmutCode Solutions logo\"", html);
        Assert.Contains("https://licenses.example.com/images/helmutcode-logo.png", html);
        Assert.Contains("color-scheme", html);
    }

    [Fact]
    public void Invoice_ShowsAmountAlreadyPaidAndBalance()
    {
        var invoice = Invoice(tax: 0m);
        invoice.Status = InvoiceStatus.PartiallyPaid;
        invoice.Receipts.Add(new Receipt { InvoiceId = invoice.Id, ReceiptNumber = "RCP-1", AmountPaid = 50m, Status = ReceiptStatus.Posted });
        var (_, html) = templates.Invoice(Customer(), invoice, "https://pay.example/x");

        Assert.Contains("GH₵50.00", html);
        Assert.Contains("Pay GH₵100.00 now", html);
    }

    [Fact]
    public void ApplyBrand_FillsFooter_AndPayInstructionsOnlyWithOptions()
    {
        var (_, html) = templates.Invoice(Customer(), Invoice(tax: 0m), "https://pay.example/x");
        var contact = new EmailBrandContact("Brand Co", "1 Road", "Accra", "+233 1", "hi@brand.test", "brand.test");

        var with = EmailTemplateService.ApplyBrand(html, contact, hasPaymentOptions: true);
        var without = EmailTemplateService.ApplyBrand(html, contact, hasPaymentOptions: false);

        Assert.Contains("hi@brand.test", with);
        Assert.Contains("1 Road", with);
        Assert.Contains("Prefer to pay directly", with);
        Assert.DoesNotContain("Prefer to pay directly", without);
        Assert.DoesNotContain("<!--pay-instructions-->", without);
    }

    [Fact]
    public void BrandContact_TreatsLegacyPlaceholderNameAsUnset()
    {
        var contact = EmailBrandContact.From(new InvoiceBrandProfile { CompanyName = "Platform License Hub" });
        Assert.Equal("HelmutCode Solutions", contact.CompanyName);
    }

    [Fact]
    public void PlainText_DropsMarkupAndKeepsLinks()
    {
        var (_, html) = templates.Invoice(Customer(), Invoice(tax: 0m), "https://pay.example/x");
        var text = EmailPlainText.FromHtml(html);

        Assert.DoesNotContain("<", text);
        Assert.DoesNotContain("@media", text);
        Assert.Contains("https://pay.example/x", text);
        Assert.Contains("INV-2026-00007", text);
        Assert.DoesNotContain("\n\n\n", text);
    }

    [Fact]
    public void ExpiryReminder_WithOpenInvoice_HasPayButtonAndSofterSubject()
    {
        var (subject, html) = templates.ExpiryReminder(Customer(), Product(), License(), Invoice(tax: 0m), "https://pay.example/r");
        Assert.StartsWith("Reminder:", subject);
        Assert.Contains("https://pay.example/r", html);
    }

    [Fact]
    public void Suspension_ForUnpaidInvoice_IncludesPayLink()
    {
        var (_, html) = templates.StatusNotice(Customer(), Product(), EmailDeliveryKind.Suspended,
            "Invoice INV-2026-00007 is overdue.", "https://pay.example/s", Invoice(tax: 0m));
        Assert.Contains("https://pay.example/s", html);
        Assert.Contains("Why:", html);
    }

    [Fact]
    public void OrgSuspension_WithoutReason_SaysNoneRecorded()
    {
        var (_, html) = templates.StatusNotice(Customer(), null, EmailDeliveryKind.Suspended);
        Assert.Contains("no reason was recorded", html);
    }

    [Fact]
    public void PaymentReceipt_CountsThisReceiptEvenIfNotYetInCollection()
    {
        var invoice = Invoice(tax: 0m);
        invoice.Status = InvoiceStatus.PartiallyPaid;
        var receipt = new Receipt { InvoiceId = invoice.Id, ReceiptNumber = "RCP-2", AmountPaid = 100m, Status = ReceiptStatus.Posted, PaidAt = Now };
        var (_, html) = templates.PaymentReceipt(Customer(), invoice, receipt);
        Assert.Contains("GH₵50.00", html);
        Assert.Contains("receipt PDF is attached", html);
    }

    [Fact]
    public void ComplimentaryReminder_AsksToContinue_AndTrialSubjectIsShort()
    {
        Assert.Contains("Would you like to continue", templates.ComplimentaryExpiryReminder(Customer(), Product(), License()).Html);
        Assert.True(templates.TrialEndingReminder(Customer(), Product(), License()).Subject.Length <= 60);
    }

    [Fact]
    public void PasswordReset_HasClearWording()
    {
        var (subject, html) = templates.PasswordReset("admin@x.test");
        Assert.Equal("Reset your HelmutCode Solutions admin password", subject);
        Assert.Contains("expires within 24 hours", html);
    }

    [Theory]
    [InlineData(InvoiceStatus.Paid, -5, "Paid")]
    [InlineData(InvoiceStatus.Sent, 10, "None")]
    [InlineData(InvoiceStatus.Sent, -1, "Overdue")]
    [InlineData(InvoiceStatus.Overdue, 10, "Overdue")]
    [InlineData(InvoiceStatus.Void, -1, "Void")]
    public void InvoiceStamp_OnlyWhenItApplies(InvoiceStatus status, int dueInDays, string expected)
    {
        var invoice = Invoice(tax: 0m);
        invoice.Status = status;
        invoice.DueDate = Now.AddDays(dueInDays);
        Assert.Equal(expected, InvoicePdfGenerator.InvoiceStamp(invoice, Now).ToString());
    }

    [Fact]
    public void InvoiceStamp_PartPaidWhenNotDue()
    {
        var invoice = Invoice(tax: 0m);
        invoice.Status = InvoiceStatus.PartiallyPaid;
        invoice.DueDate = Now.AddDays(5);
        invoice.Receipts.Add(new Receipt { InvoiceId = "i", AmountPaid = 20m, Status = ReceiptStatus.Posted, ReceiptNumber = "R" });
        Assert.Equal("PartiallyPaid", InvoicePdfGenerator.InvoiceStamp(invoice, Now).ToString());
    }

    [Fact]
    public void InvoicePdf_WithQrAndReceiptPdf_AreValidAndSmall()
    {
        var gen = new InvoicePdfGenerator();
        var letterhead = new InvoiceLetterhead("HelmutCode Solutions", "YN13 Flask St", "Accra, Ghana", "+233500785153",
            "helmutcode.runasp.net", null, [], "helmutessien@gmail.com", "C0012345678");
        var invoice = Invoice(tax: 0m);

        var pdf = gen.Generate(invoice, Customer(), letterhead, "https://pay.example/q");
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.InRange(pdf.Length, 1_000, 400_000);

        var receipt = new Receipt { InvoiceId = invoice.Id, ReceiptNumber = "RCP-2026-00001", AmountPaid = 150m, Status = ReceiptStatus.Posted, PaidAt = Now, PaymentReference = "PSK_123" };
        var receiptPdf = ((IReceiptPdfGenerator)gen).GenerateReceipt(receipt, invoice, Customer(), letterhead);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(receiptPdf, 0, 4));
        Assert.InRange(receiptPdf.Length, 1_000, 400_000);
    }

    private static Customer Customer() => new() { Name = "Acme Ltd", ContactEmail = "owner@acme.test" };
    private static ServiceProduct Product() => new() { Name = "Hostel", Code = "HOSTEL" };
    private static License License() => new()
    {
        CustomerId = "c1", ServiceProductId = "p1", PlanName = "Pro", Status = LicenseStatus.Active,
        ExpiresAt = new DateTime(2026, 12, 2, 0, 0, 0, DateTimeKind.Utc)
    };

    private static Invoice Invoice(decimal tax) => new()
    {
        CustomerId = "c1",
        InvoiceNumber = "INV-2026-00007",
        Status = InvoiceStatus.Sent,
        Currency = "GHS",
        IssueDate = Now,
        DueDate = new DateTime(2026, 12, 2, 0, 0, 0, DateTimeKind.Utc),
        PlanName = "Pro",
        Description = "Hostel renewal",
        Subtotal = 150m,
        TaxAmount = tax,
        TotalAmount = 150m + tax
    };
}
