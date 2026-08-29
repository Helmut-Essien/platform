using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.Auth;

public class ForgotPasswordRequest
{
    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email address")]
    public required string Email { get; set; }
}
