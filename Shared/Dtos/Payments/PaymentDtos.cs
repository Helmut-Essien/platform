using Platform.Shared.Enums;

namespace Platform.Shared.Dtos.Payments;

/// <summary>Whether online payments are configured, plus the starter-account collection progress.</summary>
public class OnlinePaymentsStatusDto
{
    public bool Enabled { get; set; }

    public bool TestMode { get; set; }

    public string Provider { get; set; } = "paystack";

    /// <summary>Only GHS invoices can be paid online.</summary>
    public string Currency { get; set; } = "GHS";

    /// <summary>0 means no cap is tracked (dashboard card hidden).</summary>
    public decimal CollectionCap { get; set; }

    /// <summary>Total collected through the provider (Paid + NeedsReview checkouts), lifetime.</summary>
    public decimal CollectedTotal { get; set; }
}

public class CheckoutSessionDto
{
    public required string Reference { get; set; }

    public required string AuthorizationUrl { get; set; }

    public decimal Amount { get; set; }

    public required string Currency { get; set; }

    /// <summary>True when an open checkout for the same amount was reused instead of creating a new one.</summary>
    public bool Reused { get; set; }
}

public class CheckoutStatusDto
{
    public required string Reference { get; set; }

    public PaymentCheckoutStatus Status { get; set; }

    public decimal Amount { get; set; }

    public required string Currency { get; set; }

    public string? ReceiptId { get; set; }

    public string? Message { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? PaidAt { get; set; }
}
