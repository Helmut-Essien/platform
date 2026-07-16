namespace Platform.Shared.Dtos.Settings;

public class InvoiceBrandDto
{
    public required string Id { get; set; }

    public required string CompanyName { get; set; }

    public string? AddressLine1 { get; set; }

    public string? AddressLine2 { get; set; }

    public string? Phone { get; set; }

    public string? Website { get; set; }

    public bool HasCustomLogo { get; set; }

    public string? LogoContentType { get; set; }

    public DateTime UpdatedAt { get; set; }
}
