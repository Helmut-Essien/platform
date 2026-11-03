using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.Licenses;

public class ValidateLicenseRequest
{
    [Required(ErrorMessage = "License key is required")]
    [MaxLength(200, ErrorMessage = "License key must be 200 characters or less")]
    public required string LicenseKey { get; set; }

    [MaxLength(50, ErrorMessage = "Service code must be 50 characters or less")]
    public string? ServiceCode { get; set; }

    /// <summary>
    /// Optional stable identifier of the machine/installation (e.g. a hash of the OS machine id). When sent, the
    /// device is activated against the license's device limit and bound into the signed token.
    /// </summary>
    [MaxLength(200, ErrorMessage = "Device id must be 200 characters or less")]
    public string? DeviceId { get; set; }

    /// <summary>Optional human-readable device name shown to the admin (e.g. "Front desk PC").</summary>
    [MaxLength(100, ErrorMessage = "Device name must be 100 characters or less")]
    public string? DeviceName { get; set; }

    /// <summary>
    /// Optional email to confirm against the license's customer (billing or contact email; trimmed,
    /// case-insensitive). Platform only answers whether it matches (<c>emailMatches</c> / reason
    /// <c>email_mismatch</c>); the stored email is never returned. Omit it for a plain license check.
    /// </summary>
    [MaxLength(254, ErrorMessage = "Email must be 254 characters or less")]
    public string? Email { get; set; }
}
