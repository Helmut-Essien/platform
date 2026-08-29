using System.Net;
using Platform.Api.Entities;
using Platform.Shared.Enums;

namespace Platform.Api.Services.Email;

public class EmailTemplateService
{
    private const string VendorName = "HelmutCode Solutions";

    public (string Subject, string Html) Welcome(Customer customer) =>
        ($"Welcome from {VendorName}",
            Wrap(customer.Name,
                $"Your organisation is set up with {VendorName}.",
                $"Thank you for choosing {VendorName}.",
                $"""
                <p style="margin:0 0 16px;">We have registered <strong>{WebUtility.HtmlEncode(customer.Name)}</strong> as a customer organisation on our systems.</p>
                <p style="margin:0 0 16px;">You will receive separate emails when a product license is issued or an invoice is ready. If someone on your team needs the technical contact for license keys updated, reply to this message or contact us and we will arrange it.</p>
                <p style="margin:0;">We look forward to working with you.</p>
                """));

    public (string Subject, string Html) LicenseKey(
        Customer customer,
        ServiceProduct product,
        License license,
        string key,
        bool rotated)
    {
        var productName = WebUtility.HtmlEncode(product.Name);
        var planName = WebUtility.HtmlEncode(license.PlanName);
        var subject = rotated
            ? $"Your {product.Name} license key has been replaced"
            : $"Your {product.Name} license is ready";
        var lead = rotated
            ? $"A new license key has been issued for your {productName} subscription."
            : $"Your {productName} license ({planName}) is ready to use.";
        var note = rotated
            ? "The previous key is no longer valid. Update your application configuration with the new key as soon as possible."
            : "Store this key securely. For security reasons we cannot retrieve it later — if it is lost, contact us and we will issue a replacement.";
        var html = Wrap(
            customer.Name,
            subject,
            lead,
            $"""
            <p style="margin:0 0 8px;color:#55604e;font-size:14px;">Your license key</p>
            <div style="margin:0 0 16px;padding:16px;border:1px solid #d9e2d3;border-radius:6px;background:#f5f8f2;font-family:'Courier New',Courier,monospace;font-size:17px;line-height:1.5;overflow-wrap:anywhere;"><strong>{WebUtility.HtmlEncode(key)}</strong></div>
            <p style="margin:0 0 16px;">{WebUtility.HtmlEncode(note)}</p>
            <p style="margin:0;">If you need help activating your software, contact {WebUtility.HtmlEncode(VendorName)} and include your organisation name and the product above.</p>
            """);
        return (subject, html);
    }

    public (string Subject, string Html) Renewal(Customer customer, ServiceProduct product, License license)
    {
        var productName = WebUtility.HtmlEncode(product.Name);
        var expiryHtml = license.ExpiresAt.HasValue
            ? $"The new expiry date is <strong>{license.ExpiresAt:MMMM dd, yyyy}</strong>."
            : "Your license does not have a fixed expiry date.";
        return ($"Your {product.Name} license has been renewed",
            Wrap(customer.Name,
                $"Your {productName} license has been renewed.",
                $"Your {productName} license with {WebUtility.HtmlEncode(VendorName)} has been renewed.",
                $"""
                <p style="margin:0 0 16px;">Your existing license key remains valid — you do not need to reconfigure your application.</p>
                <p style="margin:0;">{expiryHtml}</p>
                """));
    }

    public (string Subject, string Html) ExpiryReminder(Customer customer, ServiceProduct product, License license)
    {
        var productName = WebUtility.HtmlEncode(product.Name);
        var expiry = license.ExpiresAt?.ToString("MMMM dd, yyyy") ?? "soon";
        return ($"Action needed: {product.Name} license expires on {expiry}",
            Wrap(customer.Name,
                $"Your {productName} license expires soon.",
                $"Your {productName} license expires on <strong>{WebUtility.HtmlEncode(expiry)}</strong>.",
                $"""
                <p style="margin:0 0 16px;">To avoid interruption to your service, please arrange renewal with {WebUtility.HtmlEncode(VendorName)} before that date.</p>
                <p style="margin:0;">Reply to this message or contact us with your organisation name and the product above — we will help complete the renewal.</p>
                """));
    }

    public (string Subject, string Html) StatusNotice(
        Customer customer,
        ServiceProduct? product,
        EmailDeliveryKind kind,
        string? reason = null)
    {
        var productName = product is null ? null : WebUtility.HtmlEncode(product.Name);
        var (statusWord, accessDetail, nextStep) = kind switch
        {
            EmailDeliveryKind.Revoked => (
                "revoked",
                product is null
                    ? "Licensed products linked to this organisation will no longer accept your credentials until access is restored."
                    : "Product license validation will fail until access is restored.",
                $"If you believe this is a mistake or wish to reinstate access, contact {VendorName} with your organisation name."),
            EmailDeliveryKind.LicenseReactivated => (
                "reactivated",
                "License validation for this product will succeed again.",
                "You can resume normal use of the software. If anything still fails to activate, contact us and include your organisation name."),
            _ => (
                "suspended",
                product is null
                    ? "Licensed products linked to this organisation will no longer accept your credentials until access is restored."
                    : "Product license validation will fail until access is restored.",
                $"If you have questions or need access restored, contact {VendorName} with your organisation name.")
        };

        var subject = product is null
            ? $"Your organisation access has been {statusWord}"
            : $"Your {product.Name} license has been {statusWord}";
        var targetLead = product is null
            ? $"your organisation's access with {WebUtility.HtmlEncode(VendorName)}"
            : $"your {productName} license";
        var detail = string.IsNullOrWhiteSpace(reason)
            ? string.Empty
            : $"<p style=\"margin:0 0 16px;\"><strong>Reason:</strong> {WebUtility.HtmlEncode(AsSentence(reason))}</p>";
        return (subject, Wrap(
            customer.Name,
            $"Your access has been {statusWord}.",
            $"This message confirms that {targetLead} has been <strong>{statusWord}</strong>.",
            $"""
            {detail}
            <p style="margin:0 0 16px;">{WebUtility.HtmlEncode(accessDetail)}</p>
            <p style="margin:0;">{WebUtility.HtmlEncode(nextStep)}</p>
            """));
    }

    public (string Subject, string Html) PaymentReceipt(Customer customer, Invoice invoice, Receipt receipt) =>
        ($"Payment received — receipt {receipt.ReceiptNumber}",
            Wrap(customer.Name,
                $"We received your payment for invoice {WebUtility.HtmlEncode(invoice.InvoiceNumber)}.",
                $"Thank you. We received your payment of <strong>{FormatMoney(receipt.AmountPaid, invoice.Currency)}</strong>.",
                $"""
                <table role="presentation" style="width:100%;border-collapse:collapse;margin:0;">
                  {DetailRow("Receipt", receipt.ReceiptNumber)}
                  {DetailRow("Invoice", invoice.InvoiceNumber)}
                  {DetailRow("Payment date", receipt.PaidAt.ToString("MMMM dd, yyyy"))}
                  {DetailRow("Amount", FormatMoney(receipt.AmountPaid, invoice.Currency), encodeValue: false, emphasize: true)}
                </table>
                <p style="margin:16px 0 0;">Keep this email for your records. If you have a billing question, contact {WebUtility.HtmlEncode(VendorName)} and quote the invoice number above.</p>
                """));

    public (string Subject, string Html) Invoice(Customer customer, Invoice invoice)
    {
        var dueDate = invoice.DueDate?.ToString("MMMM dd, yyyy") ?? "Upon receipt";
        var subject = $"Invoice {invoice.InvoiceNumber} from {VendorName}";
        var html = Wrap(
            customer.Name,
            $"Invoice {WebUtility.HtmlEncode(invoice.InvoiceNumber)} is ready.",
            $"A new invoice has been issued by {WebUtility.HtmlEncode(VendorName)}. The invoice PDF is attached for your records.",
            $"""
            <table role="presentation" style="width:100%;border-collapse:collapse;margin:0;">
              {DetailRow("Invoice", invoice.InvoiceNumber)}
              {DetailRow("Issue date", invoice.IssueDate.ToString("MMMM dd, yyyy"))}
              {DetailRow("Due date", dueDate)}
              {DetailRow("Plan", invoice.PlanName ?? "—")}
              {DetailRow("Description", invoice.Description ?? "—")}
              {DetailRow("Subtotal", FormatMoney(invoice.Subtotal, invoice.Currency), encodeValue: false)}
              {DetailRow("Tax", FormatMoney(invoice.TaxAmount, invoice.Currency), encodeValue: false)}
              {DetailRow("Total", FormatMoney(invoice.TotalAmount, invoice.Currency), encodeValue: false, emphasize: true)}
            </table>
            <p style="margin:16px 0 0;">Please arrange payment by the due date using the details in the attached PDF. For questions about this invoice, contact us and quote the invoice number above.</p>
            """);
        return (subject, html);
    }

    private static string Wrap(string customerName, string preheader, string lead, string content) =>
        $"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width,initial-scale=1">
          <title>{VendorName}</title>
        </head>
        <body style="margin:0;padding:0;background:#f2f4f0;color:#1f271b;font-family:Arial,Helvetica,sans-serif;">
          <div style="display:none;max-height:0;overflow:hidden;opacity:0;color:transparent;">{preheader}&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;</div>
          <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="width:100%;border-collapse:collapse;background:#f2f4f0;">
            <tr>
              <td align="center" style="padding:24px 12px;">
                <table role="presentation" width="600" cellspacing="0" cellpadding="0" style="width:100%;max-width:600px;border-collapse:collapse;background:#ffffff;border:1px solid #dfe5db;border-radius:8px;overflow:hidden;">
                  <tr>
                    <td style="padding:20px 28px;background:#10150c;border-bottom:4px solid #92d959;color:#ffffff;font-size:18px;font-weight:700;letter-spacing:.2px;">{VendorName}</td>
                  </tr>
                  <tr>
                    <td style="padding:28px;font-size:16px;line-height:1.6;">
                      <p style="margin:0 0 16px;">Hello {WebUtility.HtmlEncode(customerName)},</p>
                      <p style="margin:0 0 20px;font-size:18px;line-height:1.5;font-weight:600;">{lead}</p>
                      {content}
                      <p style="margin:24px 0 0;">Regards,<br><strong>{VendorName}</strong></p>
                    </td>
                  </tr>
                  <tr>
                    <td style="padding:16px 28px;background:#f5f8f2;border-top:1px solid #dfe5db;color:#667060;font-size:12px;line-height:1.5;">This is an automated message about your licenses, invoices, or payments with {VendorName}.</td>
                  </tr>
                </table>
              </td>
            </tr>
          </table>
        </body>
        </html>
        """;

    private static string DetailRow(string label, string value, bool encodeValue = true, bool emphasize = false)
    {
        var renderedValue = encodeValue ? WebUtility.HtmlEncode(value) : value;
        var valueStyle = emphasize ? "font-weight:700;color:#1f271b;" : string.Empty;
        return $"""
          <tr>
            <td style="width:36%;padding:9px 12px;border:1px solid #dfe5db;background:#f5f8f2;color:#55604e;font-size:14px;font-weight:600;">{WebUtility.HtmlEncode(label)}</td>
            <td style="padding:9px 12px;border:1px solid #dfe5db;{valueStyle}">{renderedValue}</td>
          </tr>
        """;
    }

    private static string FormatMoney(decimal amount, string currency)
    {
        var normalizedCurrency = currency.Trim().ToUpperInvariant();
        var prefix = normalizedCurrency switch
        {
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            "GHS" => "GH₵",
            _ => $"{WebUtility.HtmlEncode(normalizedCurrency)} "
        };
        return $"{prefix}{amount:N2}";
    }

    public (string Subject, string Html) PasswordReset(string email, string resetLink) =>
        ("Reset your Platform admin password",
            Wrap(email,
                "Reset your admin password",
                "We received a request to reset the password for your Platform License Hub admin account.",
                $"""
                <p style="margin:0 0 16px;">If you made this request, use the button below. The link expires after a short period.</p>
                <p style="margin:0 0 16px;"><a href="{WebUtility.HtmlEncode(resetLink)}" style="display:inline-block;padding:12px 20px;background:#5c9f24;color:#ffffff;text-decoration:none;border-radius:6px;font-weight:600;">Reset password</a></p>
                <p style="margin:0 0 16px;font-size:14px;color:#55604e;">If the button does not work, copy this URL into your browser:<br /><span style="word-break:break-all;">{WebUtility.HtmlEncode(resetLink)}</span></p>
                <p style="margin:0;">If you did not request a reset, you can ignore this email. Your password will stay the same.</p>
                """));

    private static string AsSentence(string value)
    {
        var trimmed = value.Trim();
        return trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('?')
            ? trimmed
            : $"{trimmed}.";
    }
}
