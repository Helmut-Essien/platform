using Microsoft.EntityFrameworkCore;
using Platform.Api.Entities;
using Platform.Shared.Enums;

namespace Platform.Api.Data;

public static class SeedData
{
    // Dev-only plaintext keys — never stored in the database.
    public static readonly IReadOnlyDictionary<string, string> DevIntegrationKeys = new Dictionary<string, string>
    {
        ["HOSTEL"] = "HOSTEL-INTEGRATION-DEV-KEY-7f3a9c2e1b4d",
        ["LAUNDRY"] = "LAUNDRY-INTEGRATION-DEV-KEY-8e4b0d3f2c5a",
        ["SCHOOL"] = "SCHOOL-INTEGRATION-DEV-KEY-9f5c1e4a3d6b",
        ["ASSET"] = "ASSET-INTEGRATION-DEV-KEY-0a6d2f5b4e7c"
    };

    public static async Task SeedAsync(AppDbContext db, ILogger? logger = null, bool isDevelopment = false)
    {
        if (await db.ServiceProducts.AnyAsync())
            return;

        var now = DateTime.UtcNow;

        var products = new[]
        {
            new ServiceProduct
            {
                Name = "Hostel Management",
                Code = "HOSTEL",
                Description = "Hostel and accommodation management system",
                IsAvailableForSale = true
            },
            new ServiceProduct
            {
                Name = "Laundry App",
                Code = "LAUNDRY",
                Description = "Laundry service management application",
                IsAvailableForSale = true
            },
            new ServiceProduct
            {
                Name = "School Management",
                Code = "SCHOOL",
                Description = "School administration and management system",
                IsAvailableForSale = true
            },
            new ServiceProduct
            {
                Name = "Asset Management",
                Code = "ASSET",
                Description = "Asset tracking and management system",
                IsAvailableForSale = true
            }
        };

        db.ServiceProducts.AddRange(products);
        await db.SaveChangesAsync();

        var demoCustomer = new Customer
        {
            Name = "Acme Demo Org",
            ContactEmail = "demo@acme.example",
            ContactPhone = "+1-555-0100",
            InternalNotes = "Seeded demo customer for development",
            IsSuspended = false,
            CreatedAt = now
        };

        db.Customers.Add(demoCustomer);

        var integrationKeys = products.Select(p => new IntegrationKey
        {
            ServiceProductId = p.Id,
            KeyHash = BCrypt.Net.BCrypt.HashPassword(DevIntegrationKeys[p.Code]),
            IsActive = true,
            CreatedAt = now
        }).ToList();

        db.IntegrationKeys.AddRange(integrationKeys);

        var auditLogs = new List<AuditLog>
        {
            new()
            {
                Action = AuditAction.CustomerCreated,
                PerformedBy = "system",
                Customer = demoCustomer,
                DetailsJson = """{"source":"seed"}""",
                Timestamp = now
            }
        };

        auditLogs.AddRange(products.Select(p => new AuditLog
        {
            Action = AuditAction.IntegrationKeyCreated,
            PerformedBy = "system",
            DetailsJson = $$"""{"serviceCode":"{{p.Code}}","source":"seed"}""",
            Timestamp = now
        }));

        db.AuditLogs.AddRange(auditLogs);
        await db.SaveChangesAsync();

        if (isDevelopment && logger is not null)
        {
            foreach (var (code, key) in DevIntegrationKeys)
            {
                logger.LogWarning(
                    "DEV integration key for {ServiceCode}: {IntegrationKey} (use in X-Integration-Key header)",
                    code,
                    key);
            }
        }
    }
}
