using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Entities;
using Platform.Api.Identity;

namespace Platform.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<ServiceProduct> ServiceProducts => Set<ServiceProduct>();

    public DbSet<License> Licenses => Set<License>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<IntegrationKey> IntegrationKeys => Set<IntegrationKey>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<Receipt> Receipts => Set<Receipt>();

    public DbSet<InvoiceBrandProfile> InvoiceBrandProfiles => Set<InvoiceBrandProfile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        modelBuilder.Entity<License>()
            .HasQueryFilter(l => !l.Customer.IsSuspended);

        modelBuilder.Entity<Invoice>()
            .HasQueryFilter(i => !i.Customer.IsSuspended);

        modelBuilder.Entity<Receipt>()
            .HasQueryFilter(r => !r.Invoice.Customer.IsSuspended);
    }
}
