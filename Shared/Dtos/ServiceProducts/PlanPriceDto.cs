using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.ServiceProducts;

/// <summary>
/// A plan's default price. Only a starting point: picking the plan in Issue/Edit License fills the license's own
/// billing fields from it, and the license keeps its own copy. Changing it never changes existing licenses.
/// </summary>
public class PlanPriceDto
{
    /// <summary>Price per period before tax; must be above zero.</summary>
    [Range(typeof(decimal), "0.01", "9999999999999", ErrorMessage = "Price must be above zero")]
    public decimal Amount { get; set; }

    [Range(typeof(decimal), "0", "9999999999999", ErrorMessage = "Tax must be zero or greater")]
    public decimal TaxAmount { get; set; }

    /// <summary>3-letter code; blank = the server's default currency.</summary>
    [MaxLength(3, ErrorMessage = "Currency must be 3 characters")]
    public string? Currency { get; set; }

    [Range(1, 36, ErrorMessage = "Period must be 1 to 36 months")]
    public int PeriodMonths { get; set; } = 12;
}
