using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.Licenses;

/// <summary>Recurring price used for automatic renewal invoices. Amount null or 0 = no automatic invoices.</summary>
public class LicenseBillingDto
{
    [Range(typeof(decimal), "0", "9999999999999", ErrorMessage = "Price must be zero or greater")]
    public decimal? Amount { get; set; }

    [Range(typeof(decimal), "0", "9999999999999", ErrorMessage = "Tax must be zero or greater")]
    public decimal TaxAmount { get; set; }

    [MaxLength(3, ErrorMessage = "Currency must be 3 characters")]
    public string? Currency { get; set; }

    [Range(1, 36, ErrorMessage = "Period must be 1 to 36 months")]
    public int PeriodMonths { get; set; } = 12;

    public bool AutoRenewInvoice { get; set; } = true;
}
