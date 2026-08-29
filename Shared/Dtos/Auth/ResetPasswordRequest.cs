using System.ComponentModel.DataAnnotations;

namespace Platform.Shared.Dtos.Auth;

public class ResetPasswordRequest
{
    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email address")]
    public required string Email { get; set; }

    [Required(ErrorMessage = "Reset token is required")]
    public required string Token { get; set; }

    [Required(ErrorMessage = "New password is required")]
    [MinLength(8, ErrorMessage = "Password must be at least 8 characters")]
    public required string NewPassword { get; set; }
}
