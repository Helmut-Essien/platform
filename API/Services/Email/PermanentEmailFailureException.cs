namespace Platform.Api.Services.Email;

/// <summary>
/// A send failure that retrying cannot fix (malformed recipient, provider rejected the request as invalid).
/// The outbox worker dead-letters these immediately instead of burning every retry attempt.
/// </summary>
public class PermanentEmailFailureException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);
