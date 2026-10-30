namespace Platform.Shared.Enums;

public enum PaymentCheckoutStatus
{
    /// <summary>Created at the provider; the customer has not completed (or we have not confirmed) payment.</summary>
    Pending = 0,

    /// <summary>Verified with the provider and recorded as a receipt.</summary>
    Paid = 1,

    /// <summary>The provider reported the charge as failed or reversed.</summary>
    Failed = 2,

    /// <summary>Not paid within the checkout lifetime; a new checkout is needed.</summary>
    Expired = 3,

    /// <summary>
    /// Money was collected by the provider but could not be applied to the invoice
    /// (amount/currency mismatch, invoice voided or already paid). Needs manual review/refund.
    /// </summary>
    NeedsReview = 4,

    /// <summary>Paystack reported a refund for this payment. The receipt is NOT reversed automatically.</summary>
    Refunded = 5,

    /// <summary>The payer opened a dispute/chargeback. The receipt is NOT reversed automatically.</summary>
    Disputed = 6
}
