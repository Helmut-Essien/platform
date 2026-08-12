using Microsoft.EntityFrameworkCore;
using Platform.Api.Entities;
using Platform.Api.Security;
using Platform.Shared.Enums;

namespace Platform.Api.Data;

public static class SeedData
{
    public const string OrderFlowServiceCode = "ORDERFLOW";
    public const string OrderFlowDevLicenseKey = "ORDERFLOW-DEVK-TEST";

    // Dev-only plaintext keys — never stored in the database.
    public static readonly IReadOnlyDictionary<string, string> DevIntegrationKeys = new Dictionary<string, string>
    {
        ["HOSTEL"] = "HOSTEL-INTEGRATION-DEV-KEY-7f3a9c2e1b4d",
        ["LAUNDRY"] = "LAUNDRY-INTEGRATION-DEV-KEY-8e4b0d3f2c5a",
        ["SCHOOL"] = "SCHOOL-INTEGRATION-DEV-KEY-9f5c1e4a3d6b",
        ["ASSET"] = "ASSET-INTEGRATION-DEV-KEY-0a6d2f5b4e7c",
        [OrderFlowServiceCode] = "ORDERFLOW-INTEGRATION-DEV-KEY-1b7e3c4a5d8f"
    };

    public static async Task SeedAsync(AppDbContext db, ILogger? logger = null, bool isDevelopment = false)
    {
        if (!await db.ServiceProducts.AnyAsync())
            await SeedCatalogAndDemoCustomerAsync(db, logger, isDevelopment);

        await SeedOrderFlowAsync(db, logger, isDevelopment);
    }

    private static async Task SeedCatalogAndDemoCustomerAsync(
        AppDbContext db,
        ILogger? logger,
        bool isDevelopment)
    {

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
            },
            CreateOrderFlowProduct()
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

        var integrationKeys = products.Select(p =>
        {
            var plainKey = DevIntegrationKeys[p.Code];
            return new IntegrationKey
            {
                ServiceProductId = p.Id,
                KeyHash = BCrypt.Net.BCrypt.HashPassword(plainKey),
                KeyLookupHash = KeyLookupHasher.ComputeSha256Hex(plainKey),
                IsActive = true,
                CreatedAt = now
            };
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

    public static async Task SeedOrderFlowAsync(
        AppDbContext db,
        ILogger? logger = null,
        bool isDevelopment = false)
    {
        var now = DateTime.UtcNow;
        var product = await db.ServiceProducts.FirstOrDefaultAsync(p => p.Code == OrderFlowServiceCode);
        if (product is null)
        {
            product = CreateOrderFlowProduct();
            db.ServiceProducts.Add(product);
            db.AuditLogs.Add(new AuditLog
            {
                Action = AuditAction.ServiceProductCreated,
                PerformedBy = "system",
                DetailsJson = """{"serviceCode":"ORDERFLOW","source":"seed"}""",
                Timestamp = now
            });
            await db.SaveChangesAsync();
        }

        var plainIntegrationKey = DevIntegrationKeys[OrderFlowServiceCode];
        var hasActiveKey = await db.IntegrationKeys.AnyAsync(k =>
            k.ServiceProductId == product.Id && k.IsActive);
        if (!hasActiveKey)
        {
            db.IntegrationKeys.Add(new IntegrationKey
            {
                ServiceProductId = product.Id,
                KeyHash = BCrypt.Net.BCrypt.HashPassword(plainIntegrationKey),
                KeyLookupHash = KeyLookupHasher.ComputeSha256Hex(plainIntegrationKey),
                IsActive = true,
                CreatedAt = now
            });
            db.AuditLogs.Add(new AuditLog
            {
                Action = AuditAction.IntegrationKeyCreated,
                PerformedBy = "system",
                DetailsJson = """{"serviceCode":"ORDERFLOW","source":"seed"}""",
                Timestamp = now
            });
            await db.SaveChangesAsync();
        }

        if (isDevelopment && logger is not null)
        {
            logger.LogWarning(
                "DEV integration key for {ServiceCode}: {IntegrationKey} (use in X-Integration-Key header)",
                OrderFlowServiceCode,
                plainIntegrationKey);
        }

        if (!isDevelopment)
            return;

        var customer = await db.Customers.FirstOrDefaultAsync();
        if (customer is null)
        {
            customer = new Customer
            {
                Name = "OrderFlow Demo Shop",
                ContactEmail = "demo@orderflow.example",
                ContactPhone = "+233200000000",
                InternalNotes = "Seeded demo customer for OrderFlow development",
                IsSuspended = false,
                CreatedAt = now
            };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
        }

        var licenseLookup = KeyLookupHasher.ComputeSha256Hex(OrderFlowDevLicenseKey);
        var hasDemoLicense = await db.Licenses
            .IgnoreQueryFilters()
            .AnyAsync(l => l.ServiceProductId == product.Id && l.LicenseKeyLookupHash == licenseLookup);
        if (hasDemoLicense)
            return;

        db.Licenses.Add(new License
        {
            CustomerId = customer.Id,
            ServiceProductId = product.Id,
            Status = LicenseStatus.Active,
            PlanName = "Growth",
            LicenseKeyHash = BCrypt.Net.BCrypt.HashPassword(OrderFlowDevLicenseKey),
            LicenseKeyLookupHash = licenseLookup,
            LicenseKeySentAt = now,
            ExpiresAt = now.AddYears(1),
            CreatedAt = now,
            UpdatedAt = now
        });
        db.AuditLogs.Add(new AuditLog
        {
            Action = AuditAction.LicenseActivated,
            PerformedBy = "system",
            CustomerId = customer.Id,
            DetailsJson = """{"serviceCode":"ORDERFLOW","planName":"Growth","source":"seed"}""",
            Timestamp = now
        });
        await db.SaveChangesAsync();

        logger?.LogWarning(
            "DEV OrderFlow license key: {LicenseKey} (plan Growth, expires in 1 year)",
            OrderFlowDevLicenseKey);
    }

    private static ServiceProduct CreateOrderFlowProduct() => new()
    {
        Name = "OrderFlow",
        Code = OrderFlowServiceCode,
        Description = "WhatsApp-native order, inventory, and Mobile Money management for retailers",
        IsAvailableForSale = true
    };

    public static async Task SeedBillingAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.Invoices.AnyAsync(cancellationToken))
            return;

        var customer = await db.Customers.FirstOrDefaultAsync(cancellationToken);
        var product = await db.ServiceProducts.FirstOrDefaultAsync(p => p.Code == "HOSTEL", cancellationToken);

        if (customer is null || product is null)
            return;

        var now = DateTime.UtcNow;
        var year = now.Year;

        var invoice = new Invoice
        {
            CustomerId = customer.Id,
            ServiceProductId = product.Id,
            InvoiceNumber = $"INV-{year}-00001",
            Status = InvoiceStatus.Sent,
            IssueDate = now,
            DueDate = now.AddDays(30),
            Currency = "USD",
            Subtotal = 299.00m,
            TaxAmount = 0m,
            TotalAmount = 299.00m,
            PlanName = "Pro Annual",
            Description = "Hostel Management — Pro Annual (demo)",
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Invoices.Add(invoice);

        db.AuditLogs.Add(new AuditLog
        {
            Action = AuditAction.InvoiceSent,
            PerformedBy = "system",
            CustomerId = customer.Id,
            InvoiceId = invoice.Id,
            DetailsJson = """{"source":"seed","invoiceNumber":"INV-demo"}""",
            Timestamp = now
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}
