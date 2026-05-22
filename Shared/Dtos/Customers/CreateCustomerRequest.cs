using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.Customers;

public class CreateCustomerRequest
{
    [Required]
    [MaxLength(200)]
    public required string Name { get; set; }

    [Required]
    [EmailAddress]
    public required string ContactEmail { get; set; }

    [MaxLength(50)]
    public string? ContactPhone { get; set; }

    [MaxLength(4000)]
    public string? InternalNotes { get; set; }
}
