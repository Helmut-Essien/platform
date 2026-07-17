namespace Platform.Shared.Enums;

public enum AuditAction
{
    CustomerCreated,
    CustomerUpdated,
    CustomerSuspended,
    CustomerReactivated,
    ServiceProductCreated,
    ServiceProductUpdated,
    ServiceProductDeleted,
    LicenseIssued,
    LicenseUpdated,
    LicenseActivated,
    LicenseRenewed,
    LicenseKeyRotated,
    LicenseSuspended,
    LicenseRevoked,
    IntegrationKeyCreated,
    IntegrationKeyRevoked,
    InvoiceCreated,
    InvoiceSent,
    InvoiceVoided,
    ReceiptRecorded,
    ReceiptReversed,
    InvoiceLinkedToLicense,
    EmailDeliveryQueued,
    EmailDeliverySent,
    EmailDeliveryFailed,
    EmailDeliveryRetried,
    LicenseAutoSuspendedOverdue,
    LicenseAutoReactivatedPaid
}
