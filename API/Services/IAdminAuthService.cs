namespace Platform.Api.Services;

public interface IAdminAuthService
{
    /// <summary>
    /// Queues a password-reset email when the account exists.
    /// Always succeeds from the caller's perspective to avoid account enumeration.
    /// </summary>
    Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default);

    Task ResetPasswordAsync(
        string email,
        string token,
        string newPassword,
        CancellationToken cancellationToken = default);
}
