namespace Platform.Api.Entities;

public class InvoiceBrandProfile
{
    public string Id { get; set; } = NUlid.Ulid.NewUlid().ToString();

    public required string CompanyName { get; set; }

    public string? AddressLine1 { get; set; }

    public string? AddressLine2 { get; set; }

    public string? Phone { get; set; }

    public string? Website { get; set; }

    /// <summary>Contact email shown on invoices, receipts and email footers.</summary>
    public string? ContactEmail { get; set; }

    /// <summary>Optional tax ID (e.g. GRA TIN) printed on invoices and receipts.</summary>
    public string? TaxId { get; set; }

    /// <summary>JSON array of payment options: [{ "method": "...", "details": "..." }].</summary>
    public string? PaymentOptionsJson { get; set; }

    public byte[]? LogoBytes { get; set; }

    public string? LogoContentType { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
