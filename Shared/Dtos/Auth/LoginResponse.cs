namespace Platform.Shared.Dtos.Auth;

public class LoginResponse
{
    public required string Token { get; set; }

    public required string Email { get; set; }

    public DateTime ExpiresAt { get; set; }
}
