using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Api.Entities;
using Platform.Shared.Enums;

namespace Platform.Api.Data.EntityConfigurations;

public class LicenseConfiguration : IEntityTypeConfiguration<License>
{
    public void Configure(EntityTypeBuilder<License> builder)
    {
        builder.ToTable("Licenses");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.Id)
            .HasMaxLength(26)
            .IsRequired();

        builder.Property(l => l.CustomerId)
            .HasMaxLength(26)
            .IsRequired();

        builder.Property(l => l.ServiceProductId)
            .HasMaxLength(26)
            .IsRequired();

        builder.Property(l => l.Status)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(l => l.PlanName)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(l => l.LicenseKeyHash)
            .HasMaxLength(100);

        builder.Property(l => l.LicenseKeyLookupHash)
            .HasMaxLength(64);

        builder.HasIndex(l => new { l.ServiceProductId, l.LicenseKeyLookupHash });

        builder.Property(l => l.CreatedAt)
            .IsRequired();

        builder.Property(l => l.UpdatedAt)
            .IsRequired();

        builder.HasIndex(l => l.CustomerId);
        builder.HasIndex(l => l.ServiceProductId);
        builder.HasIndex(l => new { l.CustomerId, l.ServiceProductId });

        builder.HasOne(l => l.Customer)
            .WithMany(c => c.Licenses)
            .HasForeignKey(l => l.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(l => l.ServiceProduct)
            .WithMany(s => s.Licenses)
            .HasForeignKey(l => l.ServiceProductId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
