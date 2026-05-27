namespace Platform.Client.Services;

public sealed record ApiResult<T>(T? Data, string? ErrorMessage, bool IsSuccess)
{
    public static ApiResult<T> Ok(T data) => new(data, null, true);

    public static ApiResult<T> Fail(string? message) => new(default, message, false);
}
