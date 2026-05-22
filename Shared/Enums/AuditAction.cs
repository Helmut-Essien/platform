namespace Platform.Shared.Enums;

public enum AuditAction
{
    CustomerCreated,
    CustomerUpdated,
    CustomerSuspended,
    CustomerReactivated,
    ServiceProductCreated,
    ServiceProductUpdated,
    LicenseIssued,
    LicenseUpdated,
    LicenseActivated,
    LicenseRenewed,
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
