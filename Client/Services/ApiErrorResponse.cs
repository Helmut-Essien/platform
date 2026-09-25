namespace Platform.Client.Services;

public sealed class ApiErrorResponse
{
    public string? Message { get; set; }

    /// <summary>RFC 7807 Problem Details detail field.</summary>
    public string? Detail { get; set; }

    public string? Title { get; set; }

    public Dictionary<string, string[]>? Errors { get; set; }

    public string? DisplayMessage => Message ?? Detail;
}
