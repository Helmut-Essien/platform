using Platform.Shared.Enums;

namespace Platform.Shared.Dtos.Billing;

public class RecordReceiptRequest
{
    public decimal AmountPaid { get; set; }

    public PaymentMethod PaymentMethod { get; set; }

    public string? PaymentReference { get; set; }

    public string? Notes { get; set; }

    public DateTime? PaidAt { get; set; }
}
