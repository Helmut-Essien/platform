using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.Settings;

public class UpdateInvoiceBrandRequest
{
    [Required(ErrorMessage = "Company name is required")]
    [MaxLength(200, ErrorMessage = "Company name must be 200 characters or less")]
    public required string CompanyName { get; set; }

    [MaxLength(300, ErrorMessage = "Address line 1 must be 300 characters or less")]
    public string? AddressLine1 { get; set; }

    [MaxLength(300, ErrorMessage = "Address line 2 must be 300 characters or less")]
    public string? AddressLine2 { get; set; }

    [MaxLength(50, ErrorMessage = "Phone must be 50 characters or less")]
    public string? Phone { get; set; }

    [MaxLength(300, ErrorMessage = "Website must be 300 characters or less")]
    public string? Website { get; set; }

    /// <summary>Accepted payment methods with per-method details (shown on invoice PDFs). Max 10.</summary>
    public List<InvoicePaymentOptionDto> PaymentOptions { get; set; } = [];

    /// <summary>Optional new logo as base64 (no data: URL prefix). Max ~2MB decoded.</summary>
    public string? LogoBase64 { get; set; }

    [MaxLength(100)]
    public string? LogoContentType { get; set; }

    public bool ClearLogo { get; set; }
}
