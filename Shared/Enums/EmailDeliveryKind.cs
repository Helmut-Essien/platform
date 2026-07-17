namespace Platform.Shared.Enums;

public enum EmailDeliveryKind
{
    Welcome,
    LicenseKey,
    LicenseKeyRotated,
    RenewalConfirmation,
    Invoice,
    PaymentReceipt,
    ExpiryReminder,
    Suspended,
    Revoked
}
