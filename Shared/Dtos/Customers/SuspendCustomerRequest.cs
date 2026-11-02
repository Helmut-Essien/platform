using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.Customers;

/// <summary>Optional body for suspending a customer; the reason is shown to the customer in the notice email.</summary>
public class SuspendCustomerRequest
{
    [MaxLength(500, ErrorMessage = "Reason must be 500 characters or less")]
    public string? Reason { get; set; }
}
