using System.Globalization;
using Platform.Api.Configuration;
using Platform.Api.Entities;
using Platform.Shared.Enums;
using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Platform.Api.Services.Billing;

/// <summary>Invoice and receipt PDFs (QuestPDF, A4, embedded Lato). Logo bytes and QR codes are small and cached.</summary>
public class InvoicePdfGenerator : IInvoicePdfGenerator, IReceiptPdfGenerator
{
    private const string BundledLogoPath = "Assets/helmutcode-logo.png";

    private static readonly Color Accent = Color.FromHex("#3f7a12");
    private static readonly Color AccentMuted = Color.FromHex("#92d959");
    private static readonly Color TextPrimary = Color.FromHex("#1a1f14");
    private static readonly Color TextMuted = Color.FromHex("#4f5946");
    private static readonly Color Border = Color.FromHex("#d8e0ce");
    private static readonly Color Surface = Color.FromHex("#f4f7f0");
    private static readonly Color PaidColor = Color.FromHex("#2f6b0e");
    private static readonly Color OverdueColor = Color.FromHex("#b42318");
    private static readonly Color PartColor = Color.FromHex("#9a6700");

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly Lazy<byte[]?> BundledLogo = new(LoadBundledLogo);

    static InvoicePdfGenerator()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    // ------------------------------------------------------------------ invoice

    public byte[] Generate(Invoice invoice, Customer customer, InvoiceLetterhead letterhead, string? payUrl = null) =>
        WithLogoFallback(letterhead, logo => RenderInvoice(invoice, customer, letterhead, logo, payUrl, DateTime.UtcNow));

    public byte[] GenerateReceipt(Receipt receipt, Invoice invoice, Customer customer, InvoiceLetterhead letterhead) =>
        WithLogoFallback(letterhead, logo => RenderReceipt(receipt, invoice, customer, letterhead, logo));

    private static byte[] WithLogoFallback(InvoiceLetterhead letterhead, Func<byte[]?, byte[]> render)
    {
        var logoBytes = letterhead.LogoBytes is { Length: > 0 } ? letterhead.LogoBytes : BundledLogo.Value;
        try
        {
            return render(logoBytes);
        }
        catch (Exception) when (logoBytes is { Length: > 0 })
        {
            // A logo the image decoder rejects must not make the document itself undeliverable.
            return render(null);
        }
    }

    internal enum Stamp { None, Paid, Overdue, PartiallyPaid, Void }

    internal static Stamp InvoiceStamp(Invoice invoice, DateTime now)
    {
        if (invoice.Status == InvoiceStatus.Void)
            return Stamp.Void;
        if (invoice.Status == InvoiceStatus.Paid || (invoice.TotalAmount > 0 && AmountPaid(invoice) >= invoice.TotalAmount))
            return Stamp.Paid;
        var pastDue = invoice.DueDate is { } due && due.Date < now.Date;
        if (invoice.Status == InvoiceStatus.Overdue || (pastDue && invoice.Status is InvoiceStatus.Sent or InvoiceStatus.PartiallyPaid))
            return Stamp.Overdue;
        if (invoice.Status == InvoiceStatus.PartiallyPaid || AmountPaid(invoice) > 0)
            return Stamp.PartiallyPaid;
        return Stamp.None;
    }

    internal static decimal AmountPaid(Invoice invoice) =>
        invoice.Receipts?.Where(r => r.Status == ReceiptStatus.Posted).Sum(r => r.AmountPaid) ?? 0m;

    private static byte[] RenderInvoice(
        Invoice invoice, Customer customer, InvoiceLetterhead letterhead, byte[]? logoBytes, string? payUrl, DateTime now)
    {
        var stamp = InvoiceStamp(invoice, now);
        var paid = AmountPaid(invoice);
        var balance = Math.Max(0m, invoice.TotalAmount - paid);
        // A paid or void invoice must not invite another payment.
        var showPay = !string.IsNullOrEmpty(payUrl) && stamp is not (Stamp.Paid or Stamp.Void) && balance > 0;
        var dueDate = invoice.DueDate is { } d ? D(d) : "Upon receipt";

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(x => x.FontSize(10).FontColor(TextPrimary).LineHeight(1.25f));

                page.Header().Element(h => ComposeHeader(h, letterhead, logoBytes, "INVOICE", invoice.InvoiceNumber, stamp));
                page.Content().PaddingVertical(18).Column(col =>
                {
                    col.Spacing(0);
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Element(e => ComposeBillTo(e, customer, "BILL TO"));
                        row.ConstantItem(210).Background(Surface).Padding(12).Column(dates =>
                        {
                            DateLine(dates, "Issue date", D(invoice.IssueDate));
                            DateLine(dates, "Due date", dueDate, top: 6);
                            DateLine(dates, balance > 0 && stamp != Stamp.Void ? "Amount due" : "Total", Money(balance > 0 ? balance : invoice.TotalAmount, invoice.Currency), top: 6, strong: true);
                        });
                    });

                    col.Item().PaddingTop(22).Element(e => ComposeLineTable(e, invoice));
                    col.Item().PaddingTop(14).Row(row =>
                    {
                        row.RelativeItem().PaddingRight(16).Element(e =>
                        {
                            if (showPay)
                                ComposePayOnline(e, payUrl!, Money(balance, invoice.Currency));
                        });
                        row.ConstantItem(230).Element(e => ComposeTotals(e, invoice, paid, balance));
                    });

                    if (letterhead.PaymentOptions.Count > 0 && stamp is not (Stamp.Paid or Stamp.Void))
                        col.Item().PaddingTop(16).Element(e => ComposePaymentInfo(e, letterhead, invoice.InvoiceNumber));

                    col.Item().PaddingTop(18).Text(PaymentTerms(invoice, stamp, dueDate)).FontSize(9).FontColor(TextMuted);
                    col.Item().PaddingTop(10).Text("Thank you for your business.").Italic().FontColor(TextMuted);
                });
                page.Footer().Element(f => ComposeFooter(f, letterhead));
            });
        });
        document.WithMetadata(new DocumentMetadata
        {
            Title = $"Invoice {invoice.InvoiceNumber}",
            Author = CompanyName(letterhead),
            Subject = $"Invoice {invoice.InvoiceNumber} for {customer.Name}"
        });
        return document.GeneratePdf();
    }

    private static string PaymentTerms(Invoice invoice, Stamp stamp, string dueDate) => stamp switch
    {
        Stamp.Paid => $"This invoice has been paid in full. Thank you.",
        Stamp.Void => "This invoice has been cancelled and is not payable.",
        _ => invoice.DueDate is null
            ? $"Payment terms: due on receipt. Please quote {invoice.InvoiceNumber} as your payment reference."
            : $"Payment terms: please pay by {dueDate}. Quote {invoice.InvoiceNumber} as your payment reference."
    };

    // ------------------------------------------------------------------ receipt

    private static byte[] RenderReceipt(Receipt receipt, Invoice invoice, Customer customer, InvoiceLetterhead letterhead, byte[]? logoBytes)
    {
        var reversed = receipt.Status == ReceiptStatus.Reversed;
        var stamp = reversed ? Stamp.Void : Stamp.Paid;
        var paidTotal = AmountPaid(invoice);
        var balance = Math.Max(0m, invoice.TotalAmount - paidTotal);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(x => x.FontSize(10).FontColor(TextPrimary).LineHeight(1.25f));

                page.Header().Element(h => ComposeHeader(h, letterhead, logoBytes, "RECEIPT", receipt.ReceiptNumber, stamp,
                    reversed ? "REVERSED" : null));
                page.Content().PaddingVertical(18).Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Element(e => ComposeBillTo(e, customer, "RECEIVED FROM"));
                        row.ConstantItem(210).Background(Surface).Padding(12).Column(dates =>
                        {
                            DateLine(dates, "Payment date", D(receipt.PaidAt));
                            DateLine(dates, "Amount paid", Money(receipt.AmountPaid, invoice.Currency), top: 6, strong: true);
                        });
                    });

                    col.Item().PaddingTop(22).Border(1).BorderColor(Border).Column(t =>
                    {
                        void Line(string label, string value, bool strong = false, bool last = false)
                        {
                            var cell = t.Item().Element(e => last ? e : e.BorderBottom(1).BorderColor(Border));
                            cell.PaddingVertical(8).PaddingHorizontal(12).Row(r =>
                            {
                                r.ConstantItem(150).Text(label).FontColor(TextMuted);
                                var text = r.RelativeItem().Text(value);
                                if (strong)
                                    text.SemiBold().FontSize(12).FontColor(Accent);
                            });
                        }

                        Line("Receipt number", receipt.ReceiptNumber);
                        Line("For invoice", invoice.InvoiceNumber);
                        var forWhat = invoice.Description ?? invoice.PlanName;
                        if (!string.IsNullOrWhiteSpace(forWhat))
                            Line("Description", forWhat!);
                        Line("Payment method", MethodLabel(receipt.PaymentMethod));
                        if (!string.IsNullOrWhiteSpace(receipt.PaymentReference))
                            Line("Payment reference", receipt.PaymentReference!);
                        Line("Invoice total", Money(invoice.TotalAmount, invoice.Currency));
                        Line("Amount paid", Money(receipt.AmountPaid, invoice.Currency), strong: true);
                        Line("Balance remaining", Money(balance, invoice.Currency), last: true);
                    });

                    if (reversed)
                    {
                        col.Item().PaddingTop(14).Text(
                                $"This payment was reversed{(receipt.ReversedAt is { } r ? $" on {D(r)}" : "")}" +
                                (string.IsNullOrWhiteSpace(receipt.ReversalReason) ? "." : $": {receipt.ReversalReason}"))
                            .FontColor(OverdueColor);
                    }

                    col.Item().PaddingTop(18).Text(balance > 0
                            ? $"A balance of {Money(balance, invoice.Currency)} remains due on invoice {invoice.InvoiceNumber}."
                            : $"Invoice {invoice.InvoiceNumber} is fully paid.")
                        .FontSize(9).FontColor(TextMuted);
                    col.Item().PaddingTop(10).Text("Thank you for your payment. Please keep this receipt for your records.")
                        .Italic().FontColor(TextMuted);
                });
                page.Footer().Element(f => ComposeFooter(f, letterhead));
            });
        });
        document.WithMetadata(new DocumentMetadata
        {
            Title = $"Receipt {receipt.ReceiptNumber}",
            Author = CompanyName(letterhead),
            Subject = $"Receipt {receipt.ReceiptNumber} for invoice {invoice.InvoiceNumber}"
        });
        return document.GeneratePdf();
    }

    // ------------------------------------------------------------------ shared pieces

    private static void ComposeHeader(
        IContainer container, InvoiceLetterhead letterhead, byte[]? logoBytes, string title, string number, Stamp stamp,
        string? stampTextOverride = null)
    {
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Row(brandRow =>
                {
                    if (logoBytes is { Length: > 0 })
                    {
                        brandRow.ConstantItem(56).Height(56).Image(logoBytes).FitArea();
                        brandRow.ConstantItem(12);
                    }

                    brandRow.RelativeItem().Column(from =>
                    {
                        from.Item().Text(CompanyName(letterhead)).SemiBold().FontSize(15);
                        foreach (var line in FromLines(letterhead))
                            from.Item().Text(line).FontSize(8.5f).FontColor(TextMuted);
                    });
                });

                row.ConstantItem(190).AlignRight().Column(meta =>
                {
                    meta.Item().AlignRight().Text(title).SemiBold().FontSize(22).FontColor(Accent);
                    meta.Item().AlignRight().PaddingTop(2).Text(number).SemiBold().FontSize(11);
                    if (stamp != Stamp.None)
                    {
                        var (text, color) = stamp switch
                        {
                            Stamp.Paid => ("PAID", PaidColor),
                            Stamp.Overdue => ("OVERDUE", OverdueColor),
                            Stamp.PartiallyPaid => ("PART PAID", PartColor),
                            _ => ("VOID", TextMuted)
                        };
                        meta.Item().AlignRight().PaddingTop(8)
                            .Border(2).BorderColor(color).CornerRadius(4)
                            .PaddingVertical(3).PaddingHorizontal(10)
                            .Text(stampTextOverride ?? text).Bold().FontSize(13).LetterSpacing(0.08f).FontColor(color);
                    }
                });
            });

            col.Item().PaddingTop(12).Height(3).Background(AccentMuted);
        });
    }

    private static IEnumerable<string> FromLines(InvoiceLetterhead l)
    {
        var address = string.Join(", ", new[] { l.AddressLine1, l.AddressLine2 }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));
        if (address.Length > 0)
            yield return address;
        var contact = string.Join("  ·  ", new[] { l.Phone, l.ContactEmail }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()));
        if (contact.Length > 0)
            yield return contact;
        if (!string.IsNullOrWhiteSpace(l.Website))
            yield return l.Website!.Trim();
        if (!string.IsNullOrWhiteSpace(l.TaxId))
            yield return $"TIN: {l.TaxId!.Trim()}";
    }

    private static void ComposeBillTo(IContainer container, Customer customer, string label)
    {
        container.Column(billTo =>
        {
            billTo.Item().Text(label).SemiBold().FontSize(9).FontColor(Accent);
            billTo.Item().PaddingTop(6).Text(customer.Name).SemiBold().FontSize(12);
            billTo.Item().Text(customer.BillingEmail ?? customer.ContactEmail).FontColor(TextMuted);
            if (!string.IsNullOrWhiteSpace(customer.ContactPhone))
                billTo.Item().Text(customer.ContactPhone!).FontColor(TextMuted);
        });
    }

    private static void DateLine(ColumnDescriptor col, string label, string value, int top = 0, bool strong = false)
    {
        col.Item().PaddingTop(top).Text(label).FontSize(8).FontColor(TextMuted);
        var t = col.Item().Text(value).SemiBold();
        if (strong)
            t.FontSize(12).FontColor(Accent);
    }

    private static void ComposeLineTable(IContainer container, Invoice invoice)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(4);
                columns.RelativeColumn(3);
                columns.ConstantColumn(34);
                columns.ConstantColumn(80);
                columns.ConstantColumn(80);
            });

            table.Header(header =>
            {
                header.Cell().Element(HeaderCell).Text("Description");
                header.Cell().Element(HeaderCell).Text("Period");
                header.Cell().Element(HeaderCell).AlignRight().Text("Qty");
                header.Cell().Element(HeaderCell).AlignRight().Text("Unit price");
                header.Cell().Element(HeaderCell).AlignRight().Text("Amount");
            });

            var description = string.IsNullOrWhiteSpace(invoice.Description) ? "Software license" : invoice.Description!;
            var detail = new List<string>();
            if (!string.IsNullOrWhiteSpace(invoice.PlanName))
                detail.Add(invoice.PlanName!);
            if (invoice.ServiceProduct is { } p)
                detail.Add(string.IsNullOrWhiteSpace(p.Code) ? p.Name : $"{p.Name} ({p.Code})");

            table.Cell().Element(BodyCell).Column(c =>
            {
                c.Item().Text(description);
                if (detail.Count > 0)
                    c.Item().PaddingTop(2).Text(string.Join(" · ", detail)).FontSize(8.5f).FontColor(TextMuted);
            });
            table.Cell().Element(BodyCell).Text(PeriodText(invoice));
            table.Cell().Element(BodyCell).AlignRight().Text("1");
            table.Cell().Element(BodyCell).AlignRight().Text(Money(invoice.Subtotal, invoice.Currency));
            table.Cell().Element(BodyCell).AlignRight().Text(Money(invoice.Subtotal, invoice.Currency));
        });
    }

    internal static string PeriodText(Invoice invoice)
    {
        if (invoice.RenewalNewExpiresAt is { } newEnd && invoice.RenewalPeriodMonths is > 0)
            return $"{D(newEnd.AddMonths(-invoice.RenewalPeriodMonths.Value))} – {D(newEnd)}";
        if (invoice.RenewalForExpiresAt is { } from && invoice.RenewalPeriodMonths is > 0)
            return $"{D(from)} – {D(from.AddMonths(invoice.RenewalPeriodMonths.Value))}";
        var months = invoice.ActivationPeriodMonths;
        if (months is > 0)
            return months == 1 ? "1 month" : $"{months} months";
        return "—";
    }

    private static void ComposeTotals(IContainer container, Invoice invoice, decimal paid, decimal balance)
    {
        container.Border(1).BorderColor(Border).Padding(12).Column(col =>
        {
            void Line(string label, string value, int top = 4)
            {
                col.Item().PaddingTop(top).Row(r =>
                {
                    r.RelativeItem().Text(label);
                    r.ConstantItem(100).AlignRight().Text(value);
                });
            }

            if (invoice.TaxAmount != 0)
            {
                Line("Subtotal", Money(invoice.Subtotal, invoice.Currency), top: 0);
                Line("Tax", Money(invoice.TaxAmount, invoice.Currency));
                col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Border);
            }
            col.Item().PaddingTop(invoice.TaxAmount != 0 ? 8 : 0).Row(r =>
            {
                r.RelativeItem().Text("Total").SemiBold().FontSize(paid > 0 ? 10 : 12);
                var t = r.ConstantItem(110).AlignRight().Text(Money(invoice.TotalAmount, invoice.Currency)).SemiBold();
                if (paid <= 0)
                    t.FontSize(12).FontColor(Accent);
            });
            if (paid > 0)
            {
                Line("Amount paid", "−" + Money(paid, invoice.Currency));
                col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Border);
                col.Item().PaddingTop(8).Row(r =>
                {
                    r.RelativeItem().Text("Balance due").SemiBold().FontSize(12);
                    r.ConstantItem(110).AlignRight().Text(Money(balance, invoice.Currency)).SemiBold().FontSize(12).FontColor(Accent);
                });
            }
        });
    }

    private static void ComposePayOnline(IContainer container, string payUrl, string amount)
    {
        var qr = QrPng(payUrl);
        container.Border(1).BorderColor(Accent).Background(Surface).Padding(10).Row(row =>
        {
            if (qr is not null)
            {
                row.ConstantItem(84).Height(84).Image(qr).FitArea();
                row.ConstantItem(10);
            }
            row.RelativeItem().AlignMiddle().Column(c =>
            {
                c.Item().Text("SCAN OR CLICK TO PAY").SemiBold().FontSize(9).FontColor(Accent);
                c.Item().PaddingTop(3).Text($"Pay {amount} securely with MTN MoMo, Telecel Cash or card.").FontSize(9);
                c.Item().PaddingTop(5).Hyperlink(payUrl).Background(Accent).PaddingVertical(4).PaddingHorizontal(8)
                    .Text("Pay online now").SemiBold().FontSize(9).FontColor(Colors.White);
            });
        });
    }

    private static byte[]? QrPng(string url)
    {
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
            // 1-bit PNG, 4 px per module: a few hundred bytes, sharp at 84 pt.
            return new PngByteQRCode(data).GetGraphic(4, drawQuietZones: false);
        }
        catch
        {
            return null; // the clickable button still works without the QR code
        }
    }

    private static void ComposePaymentInfo(IContainer container, InvoiceLetterhead letterhead, string invoiceNumber)
    {
        container.Background(Surface).Border(1).BorderColor(Border).Padding(12).Column(col =>
        {
            col.Item().Text("OTHER WAYS TO PAY").SemiBold().FontSize(9).FontColor(Accent);
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
            col.Item().PaddingTop(6).Text($"Reference: {invoiceNumber}").FontSize(8.5f).FontColor(TextMuted);
        });
    }

    private static void ComposeFooter(IContainer container, InvoiceLetterhead letterhead)
    {
        container.Column(col =>
        {
            col.Item().LineHorizontal(1).LineColor(Border);
            col.Item().PaddingTop(6).Row(row =>
            {
                row.RelativeItem().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontSize(8).FontColor(TextMuted));
                    var parts = new[] { CompanyName(letterhead), letterhead.ContactEmail, letterhead.Phone, letterhead.Website }
                        .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim());
                    text.Span(string.Join("  ·  ", parts));
                });
                row.ConstantItem(70).AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontSize(8).FontColor(TextMuted));
                    text.Span("Page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });
            });
        });
    }

    private static IContainer HeaderCell(IContainer container) =>
        container.Background(Surface).BorderBottom(1).BorderColor(Border).PaddingVertical(7).PaddingHorizontal(6)
            .DefaultTextStyle(x => x.SemiBold().FontSize(9));

    private static IContainer BodyCell(IContainer container) =>
        container.BorderBottom(1).BorderColor(Border).PaddingVertical(9).PaddingHorizontal(6);

    private static string CompanyName(InvoiceLetterhead l) =>
        string.IsNullOrWhiteSpace(l.CompanyName) || l.CompanyName == BrandDefaults.LegacyPlaceholderName
            ? BrandDefaults.CompanyName
            : l.CompanyName;

    private static string D(DateTime value) => value.ToString("d MMMM yyyy", Invariant);

    private static string MethodLabel(PaymentMethod method) => method switch
    {
        PaymentMethod.MobileMoney => "Mobile Money",
        PaymentMethod.Card => "Card",
        PaymentMethod.BankTransfer => "Bank transfer",
        PaymentMethod.Cash => "Cash",
        _ => "Other"
    };

    private static byte[]? LoadBundledLogo()
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

    internal static string Money(decimal amount, string currency)
    {
        var code = (currency ?? string.Empty).Trim().ToUpperInvariant();
        var symbol = code switch
        {
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            "GHS" => "GH₵",
            _ => code + " "
        };
        return symbol + amount.ToString("N2", Invariant);
    }
}
