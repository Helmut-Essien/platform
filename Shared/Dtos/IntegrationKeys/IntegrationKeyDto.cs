namespace Platform.Shared.Dtos.IntegrationKeys;

public class IntegrationKeyDto
{
    public required string Id { get; set; }

    public required string ServiceProductId { get; set; }

    public string? ServiceProductCode { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }
}
