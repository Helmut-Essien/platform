namespace Platform.Api.Entities;

public class ServiceProduct
{
    public string Id { get; set; } = NUlid.Ulid.NewUlid().ToString();

    public required string Name { get; set; }

    public required string Code { get; set; }

    public string? Description { get; set; }

    public bool IsAvailableForSale { get; set; } = true;

    public ICollection<License> Licenses { get; set; } = [];

    public ICollection<IntegrationKey> IntegrationKeys { get; set; } = [];

    public ICollection<Invoice> Invoices { get; set; } = [];
}
