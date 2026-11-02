using System.Globalization;
using System.Net;
using System.Text;
using Platform.Api.Configuration;
using Platform.Api.Entities;
using Platform.Shared.Enums;

namespace Platform.Api.Services.Email;

/// <summary>Business contact details printed in every email footer (from the brand profile at send time).</summary>
public sealed record EmailBrandContact(
    string CompanyName,
    string? AddressLine1,
    string? AddressLine2,
    string? Phone,
    string? Email,
    string? Website)
{
    public static EmailBrandContact Defaults { get; } = new(
        BrandDefaults.CompanyName, BrandDefaults.AddressLine1, BrandDefaults.AddressLine2,
        BrandDefaults.Phone, BrandDefaults.ContactEmail, BrandDefaults.Website);

    /// <summary>The profile's company name, or the built-in name when it is blank or the legacy placeholder.</summary>
    public static string CompanyNameOrDefault(string? companyName) =>
        string.IsNullOrWhiteSpace(companyName)
        || string.Equals(companyName, BrandDefaults.LegacyPlaceholderName, StringComparison.Ordinal)
            ? BrandDefaults.CompanyName
            : companyName.Trim();

    public static EmailBrandContact From(InvoiceBrandProfile? profile)
    {
        if (profile is null)
            return Defaults;
        return new EmailBrandContact(CompanyNameOrDefault(profile.CompanyName), profile.AddressLine1, profile.AddressLine2, profile.Phone,
            profile.ContactEmail, profile.Website);
    }
}

/// <summary>
/// Customer and admin email templates. Layout rules (Gmail/Outlook safe): tables for structure, inline CSS only
/// (the &lt;style&gt; block is progressive enhancement for dark mode and small screens), bulletproof table buttons,
/// a hidden preheader, absolute image URL with alt text. The contact footer and the "pay directly" sentence are
/// marked regions that <see cref="ApplyBrand"/> fills from the brand profile when the email is sent; unfilled they
/// show the built-in defaults / nothing.
/// </summary>
public class EmailTemplateService
{
    private readonly BrandNameCache? _brandName;

    /// <summary>Company name from the brand profile (plain text, for subjects).</summary>
    private string VendorName => _brandName?.Current ?? EmailBrandContact.Defaults.CompanyName;

    /// <summary>Company name HTML-encoded, for bodies.</summary>
    private string VendorHtml => Enc(VendorName);
    private const string DefaultPublicBaseUrl = "https://helmutcode.runasp.net";

    /// <summary>Button and accent green: white text on it is 5.2:1 (WCAG AA for normal text).</summary>
    public const string ButtonColor = "#3f7a12";

    internal const string FooterStart = "<!--brand-footer-->";
    internal const string FooterEnd = "<!--/brand-footer-->";
    internal const string PayInstructionsMarker = "<!--pay-instructions-->";
    internal const string PaymentMethodsText = "MTN MoMo, Telecel Cash or card";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private readonly string _baseUrl;

    public EmailTemplateService() : this(null)
    {
    }

    public EmailTemplateService(string? publicBaseUrl, BrandNameCache? brandName = null)
    {
        _brandName = brandName;
        _baseUrl = string.IsNullOrWhiteSpace(publicBaseUrl) || publicBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            ? DefaultPublicBaseUrl
            : publicBaseUrl.Trim().TrimEnd('/');
    }

    private string LogoUrl => $"{_baseUrl}/images/helmutcode-logo.png";

    // ---------------------------------------------------------------- customer onboarding & licenses

    public (string Subject, string Html) Welcome(Customer customer, IReadOnlyCollection<string>? productNames = null)
    {
        var products = productNames is { Count: > 0 }
            ? JoinNames(productNames.Select(Enc))
            : null;
        var forWhat = products is null
            ? "NeedleBase or the custom software we build for you"
            : products;
        return ($"Welcome to {VendorName}",
            Wrap(customer.Name,
                "Your account is set up. Here is what happens next.",
                $"Welcome! <strong>{Enc(customer.Name)}</strong> is now set up as a customer of {VendorHtml}.",
                $"""
                {P($"Thank you for choosing us for {forWhat}. Here is what to expect:")}
                {Steps(
                    "<strong>Your license key</strong> arrives in a separate email as soon as your software is ready, with simple steps to activate it.",
                    $"<strong>Invoices</strong> come by email with a PDF and a secure <strong>Pay now</strong> button ({PaymentMethodsText}).",
                    "<strong>Receipts</strong> are emailed automatically after every payment.")}
                {P("Need anything, such as a different contact for license keys or invoices? Just reply to this email or use the contact details below.", last: true)}
                """));
    }

    public (string Subject, string Html) LicenseKey(
        Customer customer,
        ServiceProduct product,
        License license,
        string key,
        bool rotated)
    {
        var productName = Enc(product.Name);
        var subject = rotated
            ? $"Your {product.Name} license key has been replaced"
            : $"Your {product.Name} license is ready";
        var lead = rotated
            ? $"We have issued a new license key for {productName}."
            : $"Your {productName} license is ready to use.";
        var validUntil = license.IsComplimentary && license.ExpiresAt is null
            ? "No end date (complimentary)"
            : license.ExpiresAt is { } exp
                ? license.IsTrial ? $"{D(exp)} (free trial)" : D(exp)
                : "No end date";
        var security = rotated
            ? $"<strong>Your previous key has stopped working.</strong> Enter the new key in {productName} as soon as possible. If you did not ask for a new key, contact us straight away."
            : $"Keep this key private: anyone who has it can activate {productName} as your organisation. We will never ask you to send it to us by phone or text message.";
        var html = Wrap(
            customer.Name,
            rotated ? $"Your new {productName} key is inside. The old key no longer works." : $"Your {productName} license key and how to activate it.",
            lead,
            $"""
            <p class="muted" style="margin:0 0 8px;color:#55604e;font-size:14px;">Your license key</p>
            <div class="panel" style="margin:0 0 20px;padding:16px;border:1px solid #d9e2d3;border-radius:6px;background:#f5f8f2;font-family:'Courier New',Courier,monospace;font-size:18px;line-height:1.5;word-break:break-all;"><strong>{Enc(key)}</strong></div>
            {Table(
                Row("Product", product.Name),
                Row("Plan", license.PlanName),
                Row("Valid until", validUntil))}
            <p style="margin:20px 0 8px;font-weight:700;">How to activate</p>
            {Steps(
                $"Open {productName} on the computer or device where it is installed.",
                "When it asks for a license key (or in its license settings), paste the key above.",
                "Save. You keep the same key when the license is renewed.")}
            {P(security)}
            {P($"Need help activating? Reply to this email with your organisation name and we will help.", last: true)}
            """);
        return (subject, html);
    }

    public (string Subject, string Html) Renewal(Customer customer, ServiceProduct product, License license, Invoice? invoice = null)
    {
        var productName = Enc(product.Name);
        var rows = new List<string>();
        if (invoice is not null)
        {
            rows.Add(Row("Invoice", invoice.InvoiceNumber));
            rows.Add(Row("Amount paid", Money(invoice.TotalAmount, invoice.Currency), encodeValue: false));
            var end = invoice.RenewalNewExpiresAt ?? license.ExpiresAt;
            var months = invoice.RenewalPeriodMonths ?? invoice.ActivationPeriodMonths;
            if (end is { } e && months is > 0)
                rows.Add(Row("Period covered", $"{D(e.AddMonths(-months.Value))} – {D(e)}"));
        }
        rows.Add(Row("Valid until", license.ExpiresAt is { } x ? D(x) : "No end date", emphasize: true));
        return ($"Your {product.Name} license has been renewed",
            Wrap(customer.Name,
                license.ExpiresAt is { } ex ? $"{productName} is renewed until {D(ex)}. Thank you!" : $"{productName} is renewed. Thank you!",
                $"Thank you! Your {productName} license has been renewed.",
                $"""
                {Table(rows.ToArray())}
                {P("Your existing license key keeps working, so there is nothing to change in your software.", last: true, top: 20)}
                """));
    }

    // ---------------------------------------------------------------- reminders

    public (string Subject, string Html) ExpiryReminder(
        Customer customer, ServiceProduct product, License license, Invoice? openInvoice = null, string? payUrl = null)
    {
        if (license.IsComplimentary)
            return ComplimentaryExpiryReminder(customer, product, license);
        var productName = Enc(product.Name);
        var expiry = license.ExpiresAt is { } e ? D(e) : "soon";
        string next;
        if (openInvoice is not null)
        {
            var due = AmountDue(openInvoice);
            var amount = Money(due, openInvoice.Currency);
            next = $"""
                {P($"Your renewal invoice <strong>{Enc(openInvoice.InvoiceNumber)}</strong> for <strong>{amount}</strong> is ready.")}
                {(payUrl is null ? "" : Button(payUrl, $"Pay {amount} now") + PayMethodsNote())}
                {P($"Paying renews {productName} from {Enc(expiry)} with the same license key, so nothing changes in your software." + (payUrl is null ? " The invoice PDF (sent earlier) shows how to pay." : ""), last: true)}
                """;
        }
        else
        {
            next = P($"To keep {productName} working without a break, renew before that date. We will email your renewal invoice, or just reply to this email and we will arrange it.", last: true);
        }
        return ($"Reminder: your {product.Name} license expires on {expiry}",
            Wrap(customer.Name,
                $"Renew before {Enc(expiry)} to keep {productName} working.",
                $"Your {productName} license expires on <strong>{Enc(expiry)}</strong>.",
                next));
    }

    /// <summary>Trial ending soon, with the first invoice and its pay link (sent as the invoice email, PDF attached).</summary>
    public (string Subject, string Html) TrialEndingInvoice(
        Customer customer, ServiceProduct product, License license, Invoice invoice, string? payUrl)
    {
        var productName = Enc(product.Name);
        var endDate = license.ExpiresAt;
        var end = endDate is { } e ? D(e) : "soon";
        var shortEnd = endDate is { } s ? s.ToString("d MMM", Invariant) : "soon";
        var amount = Money(AmountDue(invoice), invoice.Currency);
        var html = Wrap(
            customer.Name,
            $"Pay {amount} by {Enc(end)} to keep using {productName}.",
            $"Your free trial of {productName} ends on <strong>{Enc(end)}</strong>.",
            $"""
            {P($"We hope {productName} has been useful. To keep using it without interruption, pay invoice <strong>{Enc(invoice.InvoiceNumber)}</strong> before your trial ends.")}
            {SummaryPanel(amount, $"Due {Enc(end)}", payUrl)}
            {Table(
                Row("Invoice", invoice.InvoiceNumber),
                Row("Plan", invoice.PlanName ?? license.PlanName),
                Row("Amount", amount, encodeValue: false, emphasize: true),
                Row("Due date", end))}
            {P("Your paid period starts when the trial ends, so paying early does not cost you any trial days. If the trial ends unpaid, access stops; paying later switches it back on with the same license key.", top: 20)}
            {LinkFallback(payUrl)}
            {PayInstructionsMarker}
            {P("The invoice PDF is attached. Questions? Just reply to this email.", last: true)}
            """);
        return ($"Your {product.Name} trial ends {shortEnd} – keep access", html);
    }

    /// <summary>Trial ending soon, for a trial with no price set (nothing to invoice automatically).</summary>
    public (string Subject, string Html) TrialEndingReminder(Customer customer, ServiceProduct product, License license)
    {
        var productName = Enc(product.Name);
        var end = license.ExpiresAt is { } e ? D(e) : "soon";
        return ($"Your {product.Name} trial ends on {end}",
            Wrap(customer.Name,
                $"Your {productName} trial ends on {Enc(end)}. Here is how to continue.",
                $"Your free trial of {productName} ends on <strong>{Enc(end)}</strong>.",
                $"""
                {P($"We hope the trial has been useful. To keep using {productName} after that date, reply to this email and we will set up your subscription.")}
                {P("You keep the same license key, so nothing changes in your software when you continue.", last: true)}
                """));
    }

    /// <summary>Complimentary (free) access ending: no payment or renewal wording, no invoice or pay link.</summary>
    public (string Subject, string Html) ComplimentaryExpiryReminder(Customer customer, ServiceProduct product, License license)
    {
        var productName = Enc(product.Name);
        var end = license.ExpiresAt is { } e ? D(e) : "soon";
        return ($"Your complimentary {product.Name} access ends on {end}",
            Wrap(customer.Name,
                $"Your free {productName} access ends on {Enc(end)}. Would you like to continue?",
                $"Your complimentary access to {productName} ends on <strong>{Enc(end)}</strong>.",
                $"""
                {P($"This access was provided free of charge by {VendorHtml}, so there is nothing to pay.")}
                {P($"<strong>Would you like to continue using {productName}?</strong> Just reply to this email and we will discuss the options with you. You would keep the same license key.", last: true)}
                """));
    }

    /// <summary>Trial extended after the customer was already told the old end date.</summary>
    public (string Subject, string Html) TrialExtended(
        Customer customer, ServiceProduct product, License license, Invoice? invoice, string? payUrl)
    {
        var productName = Enc(product.Name);
        var end = license.ExpiresAt is { } e ? D(e) : "a later date";
        var endHtml = Enc(end);
        string next;
        if (invoice is not null)
        {
            var number = Enc(invoice.InvoiceNumber);
            var amount = Money(AmountDue(invoice), invoice.Currency);
            next = $"""
                {P($"Invoice {number} for <strong>{amount}</strong> is still valid and is now due on {endHtml}. To keep using {productName} after the trial, pay it by then. Your paid period starts when the trial ends, so paying early doesn't cost you any trial days.")}
                {(payUrl is null ? "" : Button(payUrl, $"Pay {amount} now") + PayMethodsNote())}
                """;
        }
        else
        {
            next = P($"To keep using {productName} after that date, reply to this email and we will set up your subscription.");
        }
        return ($"Your {product.Name} trial has been extended to {end}",
            Wrap(customer.Name,
                $"Good news: your {productName} trial now ends on {endHtml}.",
                $"Good news: your free trial of {productName} has been extended. It now ends on <strong>{endHtml}</strong>.",
                $"""
                {next}
                {P("Your license key stays the same, so nothing changes in your software.", last: true)}
                """));
    }

    // ---------------------------------------------------------------- status changes

    public (string Subject, string Html) StatusNotice(
        Customer customer,
        ServiceProduct? product,
        EmailDeliveryKind kind,
        string? reason = null,
        string? payUrl = null,
        Invoice? invoice = null)
    {
        var productName = product is null ? null : Enc(product.Name);
        var what = productName ?? "the software linked to your organisation";
        var (statusWord, effect, nextStep) = kind switch
        {
            EmailDeliveryKind.Revoked => (
                "revoked",
                product is null
                    ? "All software linked to your organisation has stopped working."
                    : $"{productName} has stopped working with this license key, and the key cannot be used again.",
                "If you think this is a mistake, reply to this email and we will look into it."),
            EmailDeliveryKind.LicenseReactivated => (
                "reactivated",
                $"Good news: {what} works again with your existing license key.",
                "If it still shows as inactive, close and reopen it. Still stuck? Reply to this email and we will help."),
            _ => (
                "suspended",
                product is null
                    ? "All software linked to your organisation has stopped working for now."
                    : $"{productName} has stopped working for your team for now.",
                payUrl is null
                    ? "Reply to this email and we will help you restore access. Your license key will work again as soon as access is restored."
                    : "Pay the outstanding invoice below to switch it back on automatically. Your license key stays the same.")
        };

        var subject = product is null
            ? $"Your organisation access has been {statusWord}"
            : $"Your {product.Name} license has been {statusWord}";
        string reasonHtml;
        if (!string.IsNullOrWhiteSpace(reason))
            reasonHtml = P($"<strong>Why:</strong> {Enc(AsSentence(reason))}");
        else if (product is null && kind != EmailDeliveryKind.LicenseReactivated)
            reasonHtml = P("<strong>Why:</strong> no reason was recorded. Reply to this email and we will explain and help restore access.");
        else
            reasonHtml = string.Empty;

        var payHtml = string.Empty;
        if (payUrl is not null && kind == EmailDeliveryKind.Suspended)
        {
            var label = invoice is null ? "Pay now to restore access" : $"Pay {Money(AmountDue(invoice), invoice.Currency)} to restore access";
            payHtml = Button(payUrl, label) + PayMethodsNote();
        }

        var lead = kind == EmailDeliveryKind.LicenseReactivated
            ? $"{(productName is null ? "Your organisation's access" : $"Your {productName} license")} is active again."
            : $"{(productName is null ? "Your organisation's access" : $"Your {productName} license")} has been <strong>{statusWord}</strong>.";
        return (subject, Wrap(
            customer.Name,
            kind == EmailDeliveryKind.LicenseReactivated ? $"{what} works again." : $"{what} has been {statusWord}. Here is what to do next.",
            lead,
            $"""
            {P(effect)}
            {reasonHtml}
            {payHtml}
            {P(nextStep, last: true)}
            """));
    }

    // ---------------------------------------------------------------- billing

    public (string Subject, string Html) PaymentReceipt(Customer customer, Invoice invoice, Receipt receipt)
    {
        var amount = Money(receipt.AmountPaid, invoice.Currency);
        // This receipt may not be in invoice.Receipts yet (queued in the same unit of work), so count it explicitly.
        var otherPaid = invoice.Receipts?.Where(r => r.Status == ReceiptStatus.Posted && r.Id != receipt.Id).Sum(r => r.AmountPaid) ?? 0m;
        var balance = invoice.Status == InvoiceStatus.Paid ? 0m : Math.Max(0m, invoice.TotalAmount - otherPaid - receipt.AmountPaid);
        var rows = new List<string>
        {
            Row("Receipt", receipt.ReceiptNumber),
            Row("Invoice", invoice.InvoiceNumber),
        };
        var forWhat = invoice.Description ?? invoice.PlanName;
        if (!string.IsNullOrWhiteSpace(forWhat))
            rows.Add(Row("For", forWhat!));
        rows.Add(Row("Payment date", D(receipt.PaidAt)));
        rows.Add(Row("Method", MethodLabel(receipt.PaymentMethod)));
        if (!string.IsNullOrWhiteSpace(receipt.PaymentReference))
            rows.Add(Row("Reference", receipt.PaymentReference!));
        rows.Add(Row("Amount paid", amount, encodeValue: false, emphasize: true));
        if (balance > 0)
            rows.Add(Row("Balance still due", Money(balance, invoice.Currency), encodeValue: false));

        string next;
        if (balance > 0)
            next = $"A balance of <strong>{Money(balance, invoice.Currency)}</strong> is still due on invoice {Enc(invoice.InvoiceNumber)}.";
        else if (invoice.RenewalNewExpiresAt is { } renewedTo)
            next = $"Invoice {Enc(invoice.InvoiceNumber)} is fully paid and your license now runs until <strong>{D(renewedTo)}</strong>.";
        else if (invoice.ActivationAppliedAt is not null)
            next = $"Invoice {Enc(invoice.InvoiceNumber)} is fully paid and your license is now active. Your license key follows in a separate email.";
        else
            next = $"Invoice {Enc(invoice.InvoiceNumber)} is fully paid.";

        return ($"Payment received — receipt {receipt.ReceiptNumber}",
            Wrap(customer.Name,
                $"Thank you! We received {amount} for invoice {Enc(invoice.InvoiceNumber)}. Receipt attached.",
                $"Thank you! We received your payment of <strong>{amount}</strong>.",
                $"""
                {Table(rows.ToArray())}
                {P(next, top: 20)}
                {P("Your receipt PDF is attached. Keep it for your records.", last: true)}
                """));
    }

    public (string Subject, string Html) Invoice(Customer customer, Invoice invoice, string? payUrl = null)
    {
        var dueDate = invoice.DueDate is { } d ? D(d) : "Upon receipt";
        var due = AmountDue(invoice);
        var amount = Money(due, invoice.Currency);
        var subject = $"Invoice {invoice.InvoiceNumber} from {VendorName}";
        var rows = new List<string>
        {
            Row("Invoice", invoice.InvoiceNumber),
            Row("Issue date", D(invoice.IssueDate)),
            Row("Due date", dueDate),
        };
        if (!string.IsNullOrWhiteSpace(invoice.PlanName))
            rows.Add(Row("Plan", invoice.PlanName!));
        if (!string.IsNullOrWhiteSpace(invoice.Description))
            rows.Add(Row("Description", invoice.Description!));
        if (invoice.TaxAmount != 0)
        {
            rows.Add(Row("Subtotal", Money(invoice.Subtotal, invoice.Currency), encodeValue: false));
            rows.Add(Row("Tax", Money(invoice.TaxAmount, invoice.Currency), encodeValue: false));
        }
        rows.Add(Row("Total", Money(invoice.TotalAmount, invoice.Currency), encodeValue: false, emphasize: due == invoice.TotalAmount));
        if (due != invoice.TotalAmount)
        {
            rows.Add(Row("Already paid", Money(invoice.TotalAmount - due, invoice.Currency), encodeValue: false));
            rows.Add(Row("Balance due", amount, encodeValue: false, emphasize: true));
        }

        var html = Wrap(
            customer.Name,
            $"{amount} due {Enc(dueDate)}. Pay securely online in a minute.",
            $"Invoice {Enc(invoice.InvoiceNumber)} from {VendorHtml} is ready.",
            $"""
            {SummaryPanel(amount, invoice.DueDate is null ? "Due on receipt" : $"Due {Enc(dueDate)}", payUrl)}
            {Table(rows.ToArray())}
            {LinkFallback(payUrl)}
            {PayInstructionsMarker}
            {P("The invoice PDF is attached for your records. Questions about this invoice? Just reply to this email and quote the invoice number.", last: true, top: 20)}
            """);
        return (subject, html);
    }

    public (string Subject, string Html) PasswordReset(string email, string resetLinkPlaceholder = "{{RESET_LINK}}") =>
        ($"Reset your {VendorName} admin password",
            Wrap(email,
                "Use the button inside to choose a new password. The link expires within 24 hours.",
                $"We received a request to reset the password for your {VendorHtml} admin dashboard.",
                $"""
                {P("If you made this request, use the button below to choose a new password. The link works once and expires within 24 hours.")}
                {Button(resetLinkPlaceholder, "Reset password")}
                <p class="muted" style="margin:0 0 16px;font-size:14px;color:#55604e;">If the button does not work, copy this link into your browser:<br /><span style="word-break:break-all;">{resetLinkPlaceholder}</span></p>
                {P("If you did not ask for this, ignore this email. Your password stays the same.", last: true)}
                """));

    // ---------------------------------------------------------------- send-time branding

    /// <summary>
    /// Fills the marked regions from the brand profile at send time: the contact footer, and the "pay directly"
    /// sentence, which appears only when the profile lists payment options (they are printed on the PDF).
    /// </summary>
    public static string ApplyBrand(string html, EmailBrandContact contact, bool hasPaymentOptions)
    {
        if (string.IsNullOrEmpty(html))
            return html;
        var start = html.IndexOf(FooterStart, StringComparison.Ordinal);
        if (start >= 0)
        {
            var end = html.IndexOf(FooterEnd, start, StringComparison.Ordinal);
            if (end > start)
                html = string.Concat(html.AsSpan(0, start), FooterHtml(contact), html.AsSpan(end + FooterEnd.Length));
        }
        if (html.Contains(PayInstructionsMarker, StringComparison.Ordinal))
        {
            html = html.Replace(PayInstructionsMarker, hasPaymentOptions
                ? P("Prefer to pay directly? Our Mobile Money and bank details are in the attached PDF. Please quote the invoice number as the reference.", top: 16)
                : string.Empty, StringComparison.Ordinal);
        }
        return html;
    }

    // ---------------------------------------------------------------- layout helpers

    private string Wrap(string customerName, string preheader, string lead, string content) =>
        $$"""
        <!doctype html>
        <html lang="en" xmlns="http://www.w3.org/1999/xhtml">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width,initial-scale=1">
          <meta name="x-apple-disable-message-reformatting">
          <meta name="color-scheme" content="light dark">
          <meta name="supported-color-schemes" content="light dark">
          <title>{{VendorHtml}}</title>
          <style>
            :root { color-scheme: light dark; supported-color-schemes: light dark; }
            @media (max-width: 480px) {
              .content { padding: 20px 18px !important; }
              .lead { font-size: 17px !important; }
              .amount { font-size: 26px !important; }
            }
            @media (prefers-color-scheme: dark) {
              .outer, .outer-td { background: #0f130c !important; }
              .card, .header { background: #1a2015 !important; border-color: #2e3826 !important; }
              .text, .text p, .text td, .text strong, .brand { color: #e8eee2 !important; }
              .muted, .footer, .footer a { color: #b3bea9 !important; }
              .panel, .label, .footer { background: #222a1b !important; border-color: #2e3826 !important; }
              .cell { border-color: #2e3826 !important; }
              .accent { color: #9fdc6c !important; }
            }
            [data-ogsc] .card, [data-ogsc] .header { background: #1a2015 !important; }
            [data-ogsc] .text, [data-ogsc] .brand { color: #e8eee2 !important; }
            [data-ogsc] .panel, [data-ogsc] .label, [data-ogsc] .footer { background: #222a1b !important; }
          </style>
        </head>
        <body class="outer" style="margin:0;padding:0;background:#f2f4f0;color:#1f271b;font-family:Arial,Helvetica,sans-serif;">
          <div data-preheader style="display:none;max-height:0;overflow:hidden;opacity:0;color:transparent;mso-hide:all;">{{preheader}}&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;</div>
          <table role="presentation" class="outer" width="100%" cellspacing="0" cellpadding="0" border="0" style="width:100%;border-collapse:collapse;background:#f2f4f0;">
            <tr>
              <td class="outer-td" align="center" style="padding:24px 12px;">
                <table role="presentation" class="card" width="600" cellspacing="0" cellpadding="0" border="0" style="width:100%;max-width:600px;border-collapse:separate;background:#ffffff;border:1px solid #dfe5db;border-radius:8px;">
                  <tr>
                    <td class="header" style="padding:18px 28px;background:#ffffff;border-top:4px solid {{ButtonColor}};border-bottom:1px solid #dfe5db;border-radius:8px 8px 0 0;">
                      <table role="presentation" cellspacing="0" cellpadding="0" border="0"><tr>
                        <td style="padding:0 12px 0 0;vertical-align:middle;"><img src="{{LogoUrl}}" width="44" height="44" alt="{{VendorHtml}} logo" style="display:block;width:44px;height:44px;border:0;border-radius:6px;"></td>
                        <td class="brand" style="vertical-align:middle;color:#1f271b;font-size:18px;font-weight:700;font-family:Arial,Helvetica,sans-serif;">{{VendorHtml}}</td>
                      </tr></table>
                    </td>
                  </tr>
                  <tr>
                    <td class="content text" style="padding:28px;font-size:16px;line-height:1.6;color:#1f271b;">
                      <p style="margin:0 0 16px;">Hello {{Enc(customerName)}},</p>
                      <p class="lead" style="margin:0 0 20px;font-size:19px;line-height:1.45;font-weight:700;">{{lead}}</p>
                      {{content}}
                      <p style="margin:24px 0 0;">Kind regards,<br><strong>The {{VendorHtml}} team</strong></p>
                    </td>
                  </tr>
                  <tr>
                    <td class="footer" style="padding:18px 28px;background:#f5f8f2;border-top:1px solid #dfe5db;border-radius:0 0 8px 8px;color:#55604e;font-size:13px;line-height:1.6;">
                      {{FooterStart}}{{FooterHtml(EmailBrandContact.Defaults)}}{{FooterEnd}}
                    </td>
                  </tr>
                </table>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;

    private static string FooterHtml(EmailBrandContact c)
    {
        var sb = new StringBuilder();
        sb.Append("<strong>").Append(Enc(c.CompanyName)).Append("</strong>");
        var address = string.Join(", ", new[] { c.AddressLine1, c.AddressLine2 }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Enc(x!.Trim())));
        if (address.Length > 0)
            sb.Append("<br>").Append(address);
        var contacts = new List<string>();
        if (!string.IsNullOrWhiteSpace(c.Email))
            contacts.Add($"<a href=\"mailto:{Enc(c.Email!.Trim())}\" style=\"color:#55604e;text-decoration:underline;\">{Enc(c.Email!.Trim())}</a>");
        if (!string.IsNullOrWhiteSpace(c.Phone))
            contacts.Add($"<a href=\"tel:{Enc(new string(c.Phone!.Where(ch => char.IsDigit(ch) || ch == '+').ToArray()))}\" style=\"color:#55604e;text-decoration:underline;\">{Enc(c.Phone!.Trim())}</a>");
        if (!string.IsNullOrWhiteSpace(c.Website))
        {
            var site = c.Website!.Trim();
            var href = site.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? site : $"https://{site}";
            var label = site.Replace("https://", "", StringComparison.OrdinalIgnoreCase).Replace("http://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
            contacts.Add($"<a href=\"{Enc(href)}\" style=\"color:#55604e;text-decoration:underline;\">{Enc(label)}</a>");
        }
        if (contacts.Count > 0)
            sb.Append("<br>").Append(string.Join(" &middot; ", contacts));
        sb.Append("<br><span style=\"font-size:12px;\">You are receiving this service email about your account, licenses, invoices or payments with ")
            .Append(Enc(c.CompanyName)).Append(".</span>");
        return sb.ToString();
    }

    private static string SummaryPanel(string amount, string dueText, string? payUrl) =>
        $"""
        <table role="presentation" class="panel" width="100%" cellspacing="0" cellpadding="0" border="0" style="width:100%;border-collapse:separate;margin:0 0 20px;background:#f5f8f2;border:1px solid #d9e2d3;border-radius:8px;">
          <tr><td style="padding:18px 20px;">
            <p class="muted" style="margin:0;color:#55604e;font-size:14px;">Amount due</p>
            <p class="amount accent" style="margin:2px 0 4px;font-size:30px;line-height:1.2;font-weight:700;color:{ButtonColor};">{amount}</p>
            <p class="muted" style="margin:0 0 {(payUrl is null ? "0" : "16px")};color:#55604e;font-size:14px;">{dueText}</p>
            {(payUrl is null ? "" : Button(payUrl, $"Pay {amount} now", bottom: 8) + PayMethodsNote(bottom: 0))}
          </td></tr>
        </table>
        """;

    private static string Button(string url, string label, int bottom = 12) =>
        $"""
        <table role="presentation" cellspacing="0" cellpadding="0" border="0" style="margin:0 0 {bottom}px;border-collapse:separate;">
          <tr><td align="center" bgcolor="{ButtonColor}" style="background:{ButtonColor};border-radius:6px;mso-padding-alt:14px 26px;">
            <a href="{Enc(url)}" target="_blank" style="display:inline-block;padding:14px 26px;font-family:Arial,Helvetica,sans-serif;font-size:16px;line-height:20px;font-weight:700;color:#ffffff;text-decoration:none;border-radius:6px;">{label}</a>
          </td></tr>
        </table>
        """;

    private static string PayMethodsNote(int bottom = 16) =>
        $"<p class=\"muted\" style=\"margin:0 0 {bottom}px;font-size:13px;color:#55604e;\">Pay securely with {PaymentMethodsText} via Paystack.</p>";

    private static string LinkFallback(string? payUrl) =>
        string.IsNullOrEmpty(payUrl)
            ? string.Empty
            : $"<p class=\"muted\" style=\"margin:16px 0 0;font-size:13px;color:#55604e;\">Button not working? <a href=\"{Enc(payUrl)}\" style=\"color:{ButtonColor};word-break:break-all;\">Open the secure payment page</a>.</p>";

    private static string Table(params string[] rows) =>
        $"""<table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" style="width:100%;border-collapse:collapse;margin:0;">{string.Concat(rows)}</table>""";

    private static string Row(string label, string value, bool encodeValue = true, bool emphasize = false)
    {
        var renderedValue = encodeValue ? Enc(value) : value;
        var valueStyle = emphasize ? "font-weight:700;" : string.Empty;
        return $"""
          <tr>
            <td class="label cell" style="width:38%;padding:9px 12px;border:1px solid #dfe5db;background:#f5f8f2;color:#55604e;font-size:14px;font-weight:600;">{Enc(label)}</td>
            <td class="cell" style="padding:9px 12px;border:1px solid #dfe5db;{valueStyle}">{renderedValue}</td>
          </tr>
        """;
    }

    private static string Steps(params string[] items)
    {
        var sb = new StringBuilder("<table role=\"presentation\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"margin:0 0 16px;border-collapse:collapse;\">");
        for (var i = 0; i < items.Length; i++)
        {
            sb.Append($"<tr><td class=\"accent\" style=\"padding:0 10px 8px 0;vertical-align:top;font-weight:700;color:{ButtonColor};\">{i + 1}.</td>")
              .Append($"<td style=\"padding:0 0 8px;vertical-align:top;\">{items[i]}</td></tr>");
        }
        return sb.Append("</table>").ToString();
    }

    private static string P(string html, bool last = false, int top = 0) =>
        $"<p style=\"margin:{top}px 0 {(last ? 0 : 16)}px;\">{html}</p>";

    private static string JoinNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count switch
        {
            0 => string.Empty,
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1]
        };
    }

    private static string Enc(string value) => WebUtility.HtmlEncode(value);

    /// <summary>Ghana-style date: 2 December 2026.</summary>
    internal static string D(DateTime value) => value.ToString("d MMMM yyyy", Invariant);

    /// <summary>Outstanding amount from posted receipts (receipts not loaded = nothing paid yet).</summary>
    internal static decimal AmountDue(Invoice invoice)
    {
        var paid = invoice.Receipts?.Where(r => r.Status == ReceiptStatus.Posted).Sum(r => r.AmountPaid) ?? 0m;
        return Math.Max(0m, invoice.TotalAmount - paid);
    }

    internal static string MethodLabel(PaymentMethod method) => method switch
    {
        PaymentMethod.MobileMoney => "Mobile Money",
        PaymentMethod.Card => "Card",
        PaymentMethod.BankTransfer => "Bank transfer",
        PaymentMethod.Cash => "Cash",
        _ => "Other"
    };

    internal static string Money(decimal amount, string currency)
    {
        var normalizedCurrency = (currency ?? string.Empty).Trim().ToUpperInvariant();
        var prefix = normalizedCurrency switch
        {
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            "GHS" => "GH₵",
            _ => $"{Enc(normalizedCurrency)} "
        };
        return $"{prefix}{amount.ToString("N2", Invariant)}";
    }

    private static string AsSentence(string value)
    {
        var trimmed = value.Trim();
        return trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('?')
            ? trimmed
            : $"{trimmed}.";
    }
}
