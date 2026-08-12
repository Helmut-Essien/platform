using Platform.Shared.Enums;

namespace Platform.Client.Helpers;

public enum StatusTone
{
    Active,
    Pending,
    Suspended,
    Attention,
    Neutral,
    Muted
}

public static class StatusToneMapper
{
    public static (string Label, StatusTone Tone) ForCustomer(bool isSuspended) =>
        isSuspended
            ? ("Suspended", StatusTone.Suspended)
            : ("Active", StatusTone.Active);

    public static (string Label, StatusTone Tone) ForLicense(LicenseStatus status) => status switch
    {
        LicenseStatus.Active => ("Active", StatusTone.Active),
        LicenseStatus.Pending => ("Pending", StatusTone.Pending),
        LicenseStatus.Suspended => ("Suspended", StatusTone.Suspended),
        LicenseStatus.Expired => ("Expired", StatusTone.Attention),
        LicenseStatus.Revoked => ("Revoked", StatusTone.Muted),
        _ => (status.ToString(), StatusTone.Neutral)
    };

    public static (string Label, StatusTone Tone) ForInvoice(InvoiceStatus status) => status switch
    {
        InvoiceStatus.Paid => ("Paid", StatusTone.Active),
        InvoiceStatus.Sent => ("Sent", StatusTone.Pending),
        InvoiceStatus.PartiallyPaid => ("Partial", StatusTone.Pending),
        InvoiceStatus.Overdue => ("Overdue", StatusTone.Attention),
        InvoiceStatus.Draft => ("Draft", StatusTone.Muted),
        InvoiceStatus.Void => ("Void", StatusTone.Muted),
        _ => (status.ToString(), StatusTone.Neutral)
    };

    public static (string Label, StatusTone Tone) ForReceipt(ReceiptStatus status) => status switch
    {
        ReceiptStatus.Posted => ("Posted", StatusTone.Active),
        ReceiptStatus.Reversed => ("Reversed", StatusTone.Muted),
        _ => (status.ToString(), StatusTone.Neutral)
    };

    public static (string Label, StatusTone Tone) ForIntegrationKey(bool isActive) =>
        isActive
            ? ("Active", StatusTone.Active)
            : ("Revoked", StatusTone.Muted);

    public static string CssClass(StatusTone tone) => tone switch
    {
        StatusTone.Active => "status-badge--active",
        StatusTone.Pending => "status-badge--pending",
        StatusTone.Suspended => "status-badge--suspended",
        StatusTone.Attention => "status-badge--attention",
        StatusTone.Muted => "status-badge--muted",
        _ => "status-badge--neutral"
    };
}
