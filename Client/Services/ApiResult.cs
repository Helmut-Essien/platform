namespace Platform.Client.Services;

public sealed record ApiResult<T>(
    T? Data,
    string? ErrorMessage,
    bool IsSuccess,
    IReadOnlyDictionary<string, string[]>? FieldErrors = null)
{
    public static ApiResult<T> Ok(T data) => new(data, null, true);

    public static ApiResult<T> Fail(
        string? message,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null) =>
        new(default, message, false, fieldErrors);
}
