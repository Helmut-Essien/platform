namespace Platform.Api.Entities;

public class ServiceProduct
{
    public string Id { get; set; } = NUlid.Ulid.NewUlid().ToString();

    public required string Name { get; set; }

    public required string Code { get; set; }

    public string? Description { get; set; }

    public bool IsAvailableForSale { get; set; } = true;

    /// <summary>
    /// Devices a newly issued license of this product gets when the admin leaves the limit empty
    /// (0 = unlimited, null = the server-wide Licensing:DefaultMaxDevices). NeedleBase sets 1: one shop per key.
    /// </summary>
    public int? DefaultMaxDevices { get; set; }

    public ICollection<License> Licenses { get; set; } = [];

    public ICollection<IntegrationKey> IntegrationKeys { get; set; } = [];

    public ICollection<Invoice> Invoices { get; set; } = [];

    public ICollection<ProductPlan> Plans { get; set; } = [];
}
