namespace Platform.Client.Services;

public sealed class ApiErrorResponse
{
    public string? Message { get; set; }

    public Dictionary<string, string[]>? Errors { get; set; }
}
