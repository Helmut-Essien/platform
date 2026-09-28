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

    public DbSet<PaymentTransaction> PaymentTransactions => Set<PaymentTransaction>();

    public DbSet<InvoiceBrandProfile> InvoiceBrandProfiles => Set<InvoiceBrandProfile>();

    public DbSet<EmailOutboxMessage> EmailOutboxMessages => Set<EmailOutboxMessage>();

    public override int SaveChanges()
    {
        IncrementLicenseVersions();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        IncrementLicenseVersions();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void IncrementLicenseVersions()
    {
        foreach (var entry in ChangeTracker.Entries<License>())
        {
            if (entry.State == EntityState.Modified)
                entry.Entity.Version++;
        }
    }

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

        modelBuilder.Entity<PaymentTransaction>()
            .HasQueryFilter(p => !p.Invoice.Customer.IsSuspended);
    }
}
