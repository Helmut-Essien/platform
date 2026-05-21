namespace Platform.Shared.Dtos.Licenses;

public class RenewLicenseRequest
{
    public DateTime? ExpiresAt { get; set; }

    public decimal Subtotal { get; set; }

    public decimal TaxAmount { get; set; }

    public string Currency { get; set; } = "USD";

    public DateTime? DueDate { get; set; }

    public string? Description { get; set; }
}
