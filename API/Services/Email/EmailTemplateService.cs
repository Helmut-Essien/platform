using System.Net;
using Platform.Api.Entities;
using Platform.Shared.Enums;

namespace Platform.Api.Services.Email;

public class EmailTemplateService
{
    public (string Subject, string Html) Welcome(Customer customer) =>
        ("Welcome to HelmutCode Solutions Platform License Hub",
            Wrap(customer.Name,
                "Your Platform License Hub account is ready.",
                "Your customer account has been created.",
                """
                <p style="margin:0 0 16px;">You will receive separate emails when licenses or invoices are ready.</p>
                <p style="margin:0;">If you have questions, please contact your administrator.</p>
                """));

    public (string Subject, string Html) LicenseKey(
        Customer customer,
        ServiceProduct product,
        License license,
        string key,
        bool rotated)
    {
        var subject = rotated
            ? $"Your {product.Name} license key has been rotated"
            : $"Your {product.Name} license is active";
        var note = rotated
            ? "The previous key is now invalid. Update your application immediately."
            : "This key cannot be recovered. If it is lost, an administrator must rotate it and issue a new key.";
        var html = Wrap(
            customer.Name,
            subject,
            $"Your {WebUtility.HtmlEncode(product.Name)} license ({WebUtility.HtmlEncode(license.PlanName)}) is ready.",
            $"""
            <p style="margin:0 0 8px;color:#55604e;font-size:14px;">Your license key</p>
            <div style="margin:0 0 16px;padding:16px;border:1px solid #d9e2d3;border-radius:6px;background:#f5f8f2;font-family:'Courier New',Courier,monospace;font-size:17px;line-height:1.5;overflow-wrap:anywhere;"><strong>{WebUtility.HtmlEncode(key)}</strong></div>
            <p style="margin:0;">{WebUtility.HtmlEncode(note)}</p>
            """);
        return (subject, html);
    }

    public (string Subject, string Html) Renewal(Customer customer, ServiceProduct product, License license) =>
        ($"Your {product.Name} license has been renewed",
            Wrap(customer.Name,
                $"Your {WebUtility.HtmlEncode(product.Name)} license has been renewed.",
                $"Your {WebUtility.HtmlEncode(product.Name)} license has been renewed.",
                $"<p style=\"margin:0;\">Your current key remains valid. The new expiry date is <strong>{license.ExpiresAt:MMMM dd, yyyy}</strong>.</p>"));

    public (string Subject, string Html) ExpiryReminder(Customer customer, ServiceProduct product, License license) =>
        ($"{product.Name} license expires soon",
            Wrap(customer.Name,
                $"Your {WebUtility.HtmlEncode(product.Name)} license expires soon.",
                $"Your {WebUtility.HtmlEncode(product.Name)} license expires on {license.ExpiresAt:MMMM dd, yyyy}.",
                "<p style=\"margin:0;\">Please contact your administrator to arrange renewal and avoid an interruption in service.</p>"));

    public (string Subject, string Html) StatusNotice(
        Customer customer,
        ServiceProduct? product,
        EmailDeliveryKind kind,
        string? reason = null)
    {
        var (statusWord, accessDetail) = kind switch
        {
            EmailDeliveryKind.Revoked => (
                "revoked",
                product is null
                    ? "Licenses associated with this account will fail validation until an administrator restores access."
                    : "License validation will fail until an administrator restores access."),
            EmailDeliveryKind.LicenseReactivated => (
                "reactivated",
                "License validation will succeed again for this license."),
            _ => (
                "suspended",
                product is null
                    ? "Licenses associated with this account will fail validation until an administrator restores access."
                    : "License validation will fail until an administrator restores access.")
        };

        var subject = product is null
            ? $"Your platform account has been {statusWord}"
            : $"Your {product.Name} license has been {statusWord}";
        var target = product is null ? "platform account" : $"{WebUtility.HtmlEncode(product.Name)} license";
        var detail = string.IsNullOrWhiteSpace(reason)
            ? string.Empty
            : $"<p style=\"margin:0 0 16px;\"><strong>Reason:</strong> {WebUtility.HtmlEncode(AsSentence(reason))}</p>";
        return (subject, Wrap(
            customer.Name,
            $"Your {target} has been {statusWord}.",
            $"Your {target} has been {statusWord}.",
            $"{detail}<p style=\"margin:0;\">{accessDetail}</p>"));
    }

    public (string Subject, string Html) PaymentReceipt(Customer customer, Invoice invoice, Receipt receipt) =>
        ($"Receipt {receipt.ReceiptNumber} for invoice {invoice.InvoiceNumber}",
            Wrap(customer.Name,
                $"Payment received for invoice {WebUtility.HtmlEncode(invoice.InvoiceNumber)}.",
                $"We received your payment of <strong>{FormatMoney(receipt.AmountPaid, invoice.Currency)}</strong>.",
                $"""
                <table role="presentation" style="width:100%;border-collapse:collapse;margin:0;">
                  {DetailRow("Receipt", receipt.ReceiptNumber)}
                  {DetailRow("Invoice", invoice.InvoiceNumber)}
                  {DetailRow("Payment date", receipt.PaidAt.ToString("MMMM dd, yyyy"))}
                  {DetailRow("Amount", FormatMoney(receipt.AmountPaid, invoice.Currency), encodeValue: false, emphasize: true)}
                </table>
                <p style="margin:16px 0 0;">Thank you for your payment.</p>
                """));

    public (string Subject, string Html) Invoice(Customer customer, Invoice invoice)
    {
        var dueDate = invoice.DueDate?.ToString("MMMM dd, yyyy") ?? "Upon receipt";
        var subject = $"Invoice {invoice.InvoiceNumber} from Platform License Hub";
        var html = Wrap(
            customer.Name,
            $"Invoice {WebUtility.HtmlEncode(invoice.InvoiceNumber)} is ready.",
            "A new invoice has been issued for your account. The invoice PDF is attached.",
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
            <p style="margin:16px 0 0;">Download the attached PDF for your records. Thank you for your business.</p>
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
          <title>Platform License Hub</title>
        </head>
        <body style="margin:0;padding:0;background:#f2f4f0;color:#1f271b;font-family:Arial,Helvetica,sans-serif;">
          <div style="display:none;max-height:0;overflow:hidden;opacity:0;color:transparent;">{preheader}&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;</div>
          <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="width:100%;border-collapse:collapse;background:#f2f4f0;">
            <tr>
              <td align="center" style="padding:24px 12px;">
                <table role="presentation" width="600" cellspacing="0" cellpadding="0" style="width:100%;max-width:600px;border-collapse:collapse;background:#ffffff;border:1px solid #dfe5db;border-radius:8px;overflow:hidden;">
                  <tr>
                    <td style="padding:20px 28px;background:#10150c;border-bottom:4px solid #92d959;color:#ffffff;font-size:18px;font-weight:700;letter-spacing:.2px;">Platform License Hub</td>
                  </tr>
                  <tr>
                    <td style="padding:28px;font-size:16px;line-height:1.6;">
                      <p style="margin:0 0 16px;">Hello {WebUtility.HtmlEncode(customerName)},</p>
                      <p style="margin:0 0 20px;font-size:18px;line-height:1.5;font-weight:600;">{lead}</p>
                      {content}
                      <p style="margin:24px 0 0;">Regards,<br><strong>HelmutCode Solutions</strong></p>
                    </td>
                  </tr>
                  <tr>
                    <td style="padding:16px 28px;background:#f5f8f2;border-top:1px solid #dfe5db;color:#667060;font-size:12px;line-height:1.5;">This is an automated transactional message about your account, license, or billing activity.</td>
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

    private static string AsSentence(string value)
    {
        var trimmed = value.Trim();
        return trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('?')
            ? trimmed
            : $"{trimmed}.";
    }
}
