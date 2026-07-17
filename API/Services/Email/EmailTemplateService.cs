using System.Net;
using Platform.Api.Entities;
using Platform.Shared.Enums;

namespace Platform.Api.Services.Email;

public class EmailTemplateService
{
    public (string Subject, string Html) Welcome(Customer customer) =>
        ($"Welcome to Platform License Hub",
            Wrap(customer.Name,
                "Your customer account has been created.",
                "Your administrator will contact you when licenses or invoices are ready."));

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
            $"Your {WebUtility.HtmlEncode(product.Name)} license ({WebUtility.HtmlEncode(license.PlanName)}) is ready.",
            $"""
            <p>Your license key:</p>
            <p style="font-family:monospace;font-size:1.1em"><strong>{WebUtility.HtmlEncode(key)}</strong></p>
            <p>{WebUtility.HtmlEncode(note)}</p>
            """);
        return (subject, html);
    }

    public (string Subject, string Html) Renewal(Customer customer, ServiceProduct product, License license) =>
        ($"Your {product.Name} license has been renewed",
            Wrap(customer.Name,
                $"Your {WebUtility.HtmlEncode(product.Name)} license has been renewed.",
                $"The current key remains valid. New expiry: {license.ExpiresAt:MMMM dd, yyyy}."));

    public (string Subject, string Html) ExpiryReminder(Customer customer, ServiceProduct product, License license) =>
        ($"{product.Name} license expires soon",
            Wrap(customer.Name,
                $"Your {WebUtility.HtmlEncode(product.Name)} license expires on {license.ExpiresAt:MMMM dd, yyyy}.",
                "Contact your administrator to arrange renewal."));

    public (string Subject, string Html) StatusNotice(
        Customer customer,
        ServiceProduct? product,
        EmailDeliveryKind kind,
        string? reason = null)
    {
        var status = kind == EmailDeliveryKind.Revoked ? "revoked" : "suspended";
        var subject = product is null
            ? $"Your platform account has been {status}"
            : $"Your {product.Name} license has been {status}";
        var target = product is null ? "platform account" : $"{WebUtility.HtmlEncode(product.Name)} license";
        var detail = string.IsNullOrWhiteSpace(reason)
            ? "License validation will fail until an administrator restores access."
            : $"{WebUtility.HtmlEncode(reason)} License validation will fail until access is restored.";
        return (subject, Wrap(customer.Name, $"Your {target} has been {status}.", detail));
    }

    public (string Subject, string Html) PaymentReceipt(Customer customer, Invoice invoice, Receipt receipt) =>
        ($"Receipt {receipt.ReceiptNumber} for invoice {invoice.InvoiceNumber}",
            Wrap(customer.Name,
                $"We received your payment of {receipt.AmountPaid:0.00} {invoice.Currency}.",
                $"Invoice: {WebUtility.HtmlEncode(invoice.InvoiceNumber)}. Thank you."));

    private static string Wrap(string customerName, string lead, string detail) =>
        $"""
        <html><body style="font-family:sans-serif">
        <p>Hello {WebUtility.HtmlEncode(customerName)},</p>
        <p>{lead}</p>
        <p>{detail}</p>
        </body></html>
        """;
}
