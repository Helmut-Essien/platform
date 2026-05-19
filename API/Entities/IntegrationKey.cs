namespace Platform.Api.Entities;

public class IntegrationKey
{
    public string Id { get; set; } = NUlid.Ulid.NewUlid().ToString();

    public required string ServiceProductId { get; set; }

    public required string KeyHash { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastUsedAt { get; set; }

    public ServiceProduct ServiceProduct { get; set; } = null!;
}
