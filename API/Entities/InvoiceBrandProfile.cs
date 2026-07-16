namespace Platform.Api.Entities;

public class InvoiceBrandProfile
{
    public string Id { get; set; } = NUlid.Ulid.NewUlid().ToString();

    public required string CompanyName { get; set; }

    public string? AddressLine1 { get; set; }

    public string? AddressLine2 { get; set; }

    public string? Phone { get; set; }

    public string? Website { get; set; }

    /// <summary>Optional short list of accepted payment methods (e.g. Bank transfer, MoMo).</summary>
    public string? PaymentMethods { get; set; }

    /// <summary>Optional payment instructions (account numbers, reference notes, etc.).</summary>
    public string? PaymentDetails { get; set; }

    public byte[]? LogoBytes { get; set; }

    public string? LogoContentType { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
