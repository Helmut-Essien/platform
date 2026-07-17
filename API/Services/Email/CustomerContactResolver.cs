using Platform.Api.Entities;

namespace Platform.Api.Services.Email;

public static class CustomerContactResolver
{
    public static string Billing(Customer customer) =>
        Normalize(customer.BillingEmail) ?? customer.ContactEmail;

    public static string Technical(Customer customer) =>
        Normalize(customer.TechnicalEmail) ?? customer.ContactEmail;

    public static IReadOnlyList<string> Operational(Customer customer) =>
        new[] { Technical(customer), customer.ContactEmail }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
