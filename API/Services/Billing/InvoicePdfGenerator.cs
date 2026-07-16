using Platform.Api.Entities;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Platform.Api.Services.Billing;

public class InvoicePdfGenerator : IInvoicePdfGenerator
{
    private const string BundledLogoPath = "Assets/helmutcode-logo.png";

    private static readonly Color Accent = Color.FromHex("#5c9f24");
    private static readonly Color AccentMuted = Color.FromHex("#92d959");
    private static readonly Color TextPrimary = Color.FromHex("#1a1f14");
    private static readonly Color TextMuted = Color.FromHex("#5a6350");
    private static readonly Color Border = Color.FromHex("#d8e0ce");
    private static readonly Color Surface = Color.FromHex("#f4f7f0");

    static InvoicePdfGenerator()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] Generate(Invoice invoice, Customer customer, InvoiceLetterhead letterhead)
    {
        var logoBytes = letterhead.LogoBytes is { Length: > 0 }
            ? letterhead.LogoBytes
            : TryLoadBundledLogo();
        var currencySymbol = CurrencySymbol(invoice.Currency);
        var dueDate = invoice.DueDate?.ToString("MMMM dd, yyyy") ?? "Upon receipt";
        var productLabel = invoice.ServiceProduct is null
            ? null
            : string.IsNullOrWhiteSpace(invoice.ServiceProduct.Code)
                ? invoice.ServiceProduct.Name
                : $"{invoice.ServiceProduct.Name} ({invoice.ServiceProduct.Code})";

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(x => x.FontSize(10).FontColor(TextPrimary));

                page.Header().Element(header => ComposeHeader(header, letterhead, logoBytes, invoice));
                page.Content().Element(content => ComposeContent(
                    content, letterhead, customer, invoice, currencySymbol, dueDate, productLabel));
                page.Footer().Element(footer => ComposeFooter(footer, letterhead));
            });
        });

        return document.GeneratePdf();
    }

    private static void ComposeHeader(
        IContainer container,
        InvoiceLetterhead letterhead,
        byte[]? logoBytes,
        Invoice invoice)
    {
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Row(brandRow =>
                {
                    if (logoBytes is { Length: > 0 })
                    {
                        brandRow.ConstantItem(72).Height(48).Image(logoBytes).FitArea();
                        brandRow.ConstantItem(12);
                    }

                    brandRow.RelativeItem().AlignMiddle().Column(textCol =>
                    {
                        textCol.Item().Text(letterhead.CompanyName).SemiBold().FontSize(16).FontColor(TextPrimary);
                        if (!string.IsNullOrWhiteSpace(letterhead.Website))
                            textCol.Item().Text(letterhead.Website).FontSize(9).FontColor(TextMuted);
                    });
                });

                row.ConstantItem(180).AlignRight().Column(meta =>
                {
                    meta.Item().Text("INVOICE").SemiBold().FontSize(20).FontColor(Accent);
                    meta.Item().PaddingTop(4).Text(invoice.InvoiceNumber).SemiBold().FontSize(11);
                    meta.Item().Text($"Status: {invoice.Status}").FontSize(9).FontColor(TextMuted);
                });
            });

            col.Item().PaddingTop(12).Height(3).Background(AccentMuted);
        });
    }

    private static void ComposeContent(
        IContainer container,
        InvoiceLetterhead letterhead,
        Customer customer,
        Invoice invoice,
        string currencySymbol,
        string dueDate,
        string? productLabel)
    {
        container.PaddingVertical(20).Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(billTo =>
                {
                    billTo.Item().Text("BILL TO").SemiBold().FontSize(9).FontColor(Accent);
                    billTo.Item().PaddingTop(6).Text(customer.Name).SemiBold().FontSize(12);
                    billTo.Item().Text(customer.ContactEmail).FontColor(TextMuted);
                    if (!string.IsNullOrWhiteSpace(customer.ContactPhone))
                        billTo.Item().Text(customer.ContactPhone!).FontColor(TextMuted);
                });

                row.ConstantItem(200).Background(Surface).Padding(12).Column(dates =>
                {
                    dates.Item().Text("Issue date").FontSize(8).FontColor(TextMuted);
                    dates.Item().Text(invoice.IssueDate.ToString("MMMM dd, yyyy")).SemiBold();
                    dates.Item().PaddingTop(8).Text("Due date").FontSize(8).FontColor(TextMuted);
                    dates.Item().Text(dueDate).SemiBold();
                });
            });

            col.Item().PaddingTop(24).Element(e => ComposeLineTable(
                e, invoice, currencySymbol, productLabel));

            col.Item().PaddingTop(16).AlignRight().Width(220).Element(e => ComposeTotals(e, invoice, currencySymbol));

            if (HasLetterhead(letterhead))
            {
                col.Item().PaddingTop(28).Column(from =>
                {
                    from.Item().Text("From").SemiBold().FontSize(9).FontColor(Accent);
                    if (!string.IsNullOrWhiteSpace(letterhead.AddressLine1))
                        from.Item().Text(letterhead.AddressLine1!);
                    if (!string.IsNullOrWhiteSpace(letterhead.AddressLine2))
                        from.Item().Text(letterhead.AddressLine2!);
                    if (!string.IsNullOrWhiteSpace(letterhead.Phone))
                        from.Item().Text(letterhead.Phone!).FontColor(TextMuted);
                });
            }

            if (HasPaymentInfo(letterhead))
            {
                col.Item().PaddingTop(20).Element(e => ComposePaymentInfo(e, letterhead));
            }

            col.Item().PaddingTop(24).Text("Thank you for your business.")
                .Italic().FontColor(TextMuted);
        });
    }

    private static void ComposePaymentInfo(IContainer container, InvoiceLetterhead letterhead)
    {
        container.Background(Surface).Border(1).BorderColor(Border).Padding(12).Column(col =>
        {
            col.Item().Text("PAYMENT").SemiBold().FontSize(9).FontColor(Accent);

            col.Item().PaddingTop(6).Row(row =>
            {
                foreach (var option in letterhead.PaymentOptions)
                {
                    row.RelativeItem().PaddingRight(8).Column(methodCol =>
                    {
                        methodCol.Item().Text(option.Method).SemiBold();
                        if (!string.IsNullOrWhiteSpace(option.Details))
                            methodCol.Item().PaddingTop(2).Text(option.Details!);
                    });
                }
            });
        });
    }

    private static void ComposeLineTable(
        IContainer container,
        Invoice invoice,
        string currencySymbol,
        string? productLabel)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(4);
                columns.RelativeColumn(2);
                columns.ConstantColumn(90);
            });

            table.Header(header =>
            {
                header.Cell().Element(HeaderCell).Text("Description");
                header.Cell().Element(HeaderCell).Text("Plan / Product");
                header.Cell().Element(HeaderCell).AlignRight().Text("Amount");
            });

            var description = string.IsNullOrWhiteSpace(invoice.Description)
                ? "Software license"
                : invoice.Description!;
            var plan = string.IsNullOrWhiteSpace(invoice.PlanName) ? "—" : invoice.PlanName!;
            if (!string.IsNullOrWhiteSpace(productLabel))
                plan = $"{plan}\n{productLabel}";

            table.Cell().Element(BodyCell).Text(description);
            table.Cell().Element(BodyCell).Text(plan);
            table.Cell().Element(BodyCell).AlignRight().Text($"{currencySymbol}{invoice.Subtotal:N2}");
        });
    }

    private static void ComposeTotals(IContainer container, Invoice invoice, string currencySymbol)
    {
        container.Border(1).BorderColor(Border).Padding(12).Column(col =>
        {
            col.Item().Row(r =>
            {
                r.RelativeItem().Text("Subtotal");
                r.ConstantItem(90).AlignRight().Text($"{currencySymbol}{invoice.Subtotal:N2}");
            });
            col.Item().PaddingTop(4).Row(r =>
            {
                r.RelativeItem().Text("Tax");
                r.ConstantItem(90).AlignRight().Text($"{currencySymbol}{invoice.TaxAmount:N2}");
            });
            col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Border);
            col.Item().PaddingTop(8).Row(r =>
            {
                r.RelativeItem().Text("Total").SemiBold().FontSize(12);
                r.ConstantItem(90).AlignRight().Text($"{currencySymbol}{invoice.TotalAmount:N2}")
                    .SemiBold().FontSize(12).FontColor(Accent);
            });
        });
    }

    private static void ComposeFooter(IContainer container, InvoiceLetterhead letterhead)
    {
        container.Column(col =>
        {
            col.Item().LineHorizontal(1).LineColor(Border);
            col.Item().PaddingTop(8).AlignCenter().Text(text =>
            {
                text.Span(letterhead.CompanyName).FontColor(TextMuted).FontSize(8);
                if (!string.IsNullOrWhiteSpace(letterhead.Website))
                {
                    text.Span("  ·  ").FontColor(TextMuted).FontSize(8);
                    text.Span(letterhead.Website).FontColor(TextMuted).FontSize(8);
                }
            });
        });
    }

    private static IContainer HeaderCell(IContainer container) =>
        container.Background(Surface).BorderBottom(1).BorderColor(Border).PaddingVertical(8).PaddingHorizontal(6);

    private static IContainer BodyCell(IContainer container) =>
        container.BorderBottom(1).BorderColor(Border).PaddingVertical(10).PaddingHorizontal(6);

    private static bool HasLetterhead(InvoiceLetterhead letterhead) =>
        !string.IsNullOrWhiteSpace(letterhead.AddressLine1)
        || !string.IsNullOrWhiteSpace(letterhead.AddressLine2)
        || !string.IsNullOrWhiteSpace(letterhead.Phone);

    private static bool HasPaymentInfo(InvoiceLetterhead letterhead) =>
        letterhead.PaymentOptions.Count > 0;

    private static byte[]? TryLoadBundledLogo()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, BundledLogoPath);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string CurrencySymbol(string currency) => currency.ToUpperInvariant() switch
    {
        "USD" => "$",
        "EUR" => "€",
        "GBP" => "£",
        "GHS" => "GH₵",
        _ => currency.ToUpperInvariant() + " "
    };
}
