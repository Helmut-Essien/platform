using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.Customers;

public class CreateCustomerRequest
{
    [Required(ErrorMessage = "Name is required")]
    [MaxLength(200, ErrorMessage = "Name must be 200 characters or less")]
    public required string Name { get; set; }

    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Enter a valid email address")]
    public required string ContactEmail { get; set; }

    [MaxLength(50, ErrorMessage = "Phone must be 50 characters or less")]
    public string? ContactPhone { get; set; }

    [MaxLength(4000, ErrorMessage = "Internal notes must be 4000 characters or less")]
    public string? InternalNotes { get; set; }
}
