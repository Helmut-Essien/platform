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
    InvoiceLinkedToLicense
}
