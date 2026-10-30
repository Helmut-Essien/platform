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

/// <summary>What the anonymous pay page shows. Deliberately minimal: no emails, phones, addresses or line items.</summary>
public class PublicInvoiceSummaryDto
{
    public required string InvoiceNumber { get; set; }
    public required string CustomerName { get; set; }
    public string? IssuerName { get; set; }
    public required string Currency { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal AmountPaid { get; set; }
    public decimal AmountDue { get; set; }
    public DateTime DueDate { get; set; }
    /// <summary>"payable", "paid" or "closed".</summary>
    public required string State { get; set; }
    /// <summary>False when Paystack is switched off or the invoice can't be paid online.</summary>
    public bool OnlinePaymentAvailable { get; set; }
    public bool TestMode { get; set; }
}

public class PublicCheckoutStatusDto
{
    public PaymentCheckoutStatus Status { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public string? Message { get; set; }
    public DateTime? PaidAt { get; set; }
}

public class PayLinkDto
{
    public required string Url { get; set; }
}
