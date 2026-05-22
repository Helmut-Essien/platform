namespace Platform.Shared.Dtos.Customers;

public class CustomerDto
{
    public required string Id { get; set; }

    public required string Name { get; set; }

    public required string ContactEmail { get; set; }

    public string? ContactPhone { get; set; }

    public string? InternalNotes { get; set; }

    public bool IsSuspended { get; set; }

    public DateTime CreatedAt { get; set; }

    public int LicenseCount { get; set; }
}
