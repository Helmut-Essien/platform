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

        builder.HasIndex(i => i.CustomerId);
        builder.HasIndex(i => i.LicenseId);
        builder.HasIndex(i => i.Status);

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
