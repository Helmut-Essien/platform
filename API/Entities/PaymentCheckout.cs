using Platform.Shared.Enums;

namespace Platform.Api.Entities;

/// <summary>An online checkout session with a payment provider (Paystack) for one invoice.</summary>
public class PaymentCheckout
{
    public string Id { get; set; } = NUlid.Ulid.NewUlid().ToString();

    public required string InvoiceId { get; set; }

    public string Provider { get; set; } = "paystack";

    /// <summary>Our unique reference sent to the provider (also the receipt's payment reference).</summary>
    public required string Reference { get; set; }

    public decimal Amount { get; set; }

    /// <summary>Amount in minor units (pesewas) exactly as sent to the provider.</summary>
    public long AmountMinor { get; set; }

    public required string Currency { get; set; }

    public PaymentCheckoutStatus Status { get; set; } = PaymentCheckoutStatus.Pending;

    public string? AuthorizationUrl { get; set; }

    public string? Channel { get; set; }

    public string? ProviderTransactionId { get; set; }

    public string? ReceiptId { get; set; }

    public string? LastError { get; set; }

    public required string CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastCheckedAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>Amount Paystack actually charged (pesewas, may include fees passed to the payer); set on success.</summary>
    public long? PaidAmountMinor { get; set; }

    /// <summary>True when created with a live key, so test payments never count toward the live collection cap.</summary>
    public bool LiveMode { get; set; }

    /// <summary>Optimistic concurrency token: webhook, reconcile worker and on-demand status may race on one row.</summary>
    public Guid Version { get; set; } = Guid.NewGuid();

    public Invoice Invoice { get; set; } = null!;
}
