using Platform.Shared.Dtos.Audit;
using Platform.Shared.Enums;

namespace Platform.Client.Helpers;

public static class AuditDisplay
{
    public static string ActionLabel(AuditAction action) => action switch
    {
        AuditAction.CustomerCreated => "Customer created",
        AuditAction.CustomerUpdated => "Customer updated",
        AuditAction.CustomerSuspended => "Customer suspended",
        AuditAction.CustomerReactivated => "Customer reactivated",
        AuditAction.ServiceProductCreated => "Service product created",
        AuditAction.ServiceProductUpdated => "Service product updated",
        AuditAction.ServiceProductDeleted => "Service product deleted",
        AuditAction.LicenseIssued => "License issued",
        AuditAction.LicenseUpdated => "License updated",
        AuditAction.LicenseActivated => "License activated",
        AuditAction.LicenseRenewed => "License renewed",
        AuditAction.LicenseKeyRotated => "License key rotated",
        AuditAction.LicenseSuspended => "License suspended",
        AuditAction.LicenseRevoked => "License revoked",
        AuditAction.IntegrationKeyCreated => "Integration key created",
        AuditAction.IntegrationKeyRevoked => "Integration key revoked",
        AuditAction.InvoiceCreated => "Invoice created",
        AuditAction.InvoiceSent => "Invoice sent",
        AuditAction.InvoiceVoided => "Invoice voided",
        AuditAction.ReceiptRecorded => "Payment recorded",
        AuditAction.ReceiptReversed => "Payment reversed",
        AuditAction.InvoiceLinkedToLicense => "Invoice linked to license",
        AuditAction.EmailDeliveryQueued => "Email queued",
        AuditAction.EmailDeliverySent => "Email sent",
        AuditAction.EmailDeliveryFailed => "Email delivery failed",
        AuditAction.EmailDeliveryRetried => "Email delivery retried",
        AuditAction.LicenseAutoSuspendedOverdue => "License auto-suspended (overdue invoice)",
        AuditAction.LicenseAutoReactivatedPaid => "License reactivated after payment",
        _ => SplitPascal(action.ToString())
    };

    public static string Summary(AuditLogDto entry)
    {
        var actor = string.IsNullOrWhiteSpace(entry.PerformedBy) ? "system" : entry.PerformedBy.Trim();
        return actor.StartsWith("system:", StringComparison.OrdinalIgnoreCase)
            ? "Automated system action"
            : $"By {actor}";
    }

    private static string SplitPascal(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var chars = new List<char>(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(value[i - 1]))
                chars.Add(' ');
            chars.Add(i == 0 ? char.ToUpperInvariant(c) : c);
        }

        return new string(chars.ToArray());
    }
}
