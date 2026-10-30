using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Api.Entities;

namespace Platform.Api.Data.EntityConfigurations;

public class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("Invoices");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.Id)
            .HasMaxLength(26)
            .IsRequired();

        builder.Property(i => i.CustomerId)
            .HasMaxLength(26)
            .IsRequired();

        builder.Property(i => i.LicenseId)
            .HasMaxLength(26);

        builder.Property(i => i.ServiceProductId)
            .HasMaxLength(26);

        builder.Property(i => i.InvoiceNumber)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(i => i.Status)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(i => i.Currency)
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(i => i.Subtotal)
            .HasPrecision(18, 2);

        builder.Property(i => i.TaxAmount)
            .HasPrecision(18, 2);

        builder.Property(i => i.TotalAmount)
            .HasPrecision(18, 2);

        builder.Property(i => i.PlanName)
            .HasMaxLength(100);

        builder.Property(i => i.Description)
            .HasMaxLength(2000);

        builder.Property(i => i.InternalNotes)
            .HasMaxLength(4000);

        builder.Property(i => i.IssueDate)
            .IsRequired();

        builder.Property(i => i.CreatedAt)
            .IsRequired();

        builder.Property(i => i.UpdatedAt)
            .IsRequired();

        builder.HasIndex(i => i.InvoiceNumber)
            .IsUnique();

        builder.Property(i => i.PayLinkNonce).HasMaxLength(32);

        // One live renewal invoice per license and period. Void ones are excluded so an admin can re-create.
        builder.HasIndex(i => new { i.LicenseId, i.RenewalForExpiresAt })
            .IsUnique()
            .HasFilter("\"RenewalForExpiresAt\" IS NOT NULL AND \"Status\" <> 'Void'")
            .HasDatabaseName("IX_Invoices_OneRenewalPerPeriod");

        builder.HasIndex(i => i.CustomerId);
        builder.HasIndex(i => i.LicenseId);
        builder.HasIndex(i => i.Status);
        // P4: list ordering, and the overdue scan (status + due date)
        builder.HasIndex(i => i.IssueDate);
        builder.HasIndex(i => new { i.Status, i.DueDate });

        builder.HasOne(i => i.Customer)
            .WithMany(c => c.Invoices)
            .HasForeignKey(i => i.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(i => i.License)
            .WithMany(l => l.Invoices)
            .HasForeignKey(i => i.LicenseId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(i => i.ServiceProduct)
            .WithMany(s => s.Invoices)
            .HasForeignKey(i => i.ServiceProductId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
