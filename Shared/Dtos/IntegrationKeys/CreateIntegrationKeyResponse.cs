namespace Platform.Shared.Dtos.IntegrationKeys;

public class CreateIntegrationKeyResponse
{
    public required IntegrationKeyDto Key { get; set; }

    /// <summary>Plain integration key — shown only once at creation.</summary>
    public required string PlainKey { get; set; }
}
