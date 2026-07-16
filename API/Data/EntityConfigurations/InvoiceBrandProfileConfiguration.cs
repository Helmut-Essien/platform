using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Api.Entities;

namespace Platform.Api.Data.EntityConfigurations;

public class InvoiceBrandProfileConfiguration : IEntityTypeConfiguration<InvoiceBrandProfile>
{
    public void Configure(EntityTypeBuilder<InvoiceBrandProfile> builder)
    {
        builder.ToTable("InvoiceBrandProfiles");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .HasMaxLength(26)
            .IsRequired();

        builder.Property(p => p.CompanyName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(p => p.AddressLine1)
            .HasMaxLength(300);

        builder.Property(p => p.AddressLine2)
            .HasMaxLength(300);

        builder.Property(p => p.Phone)
            .HasMaxLength(50);

        builder.Property(p => p.Website)
            .HasMaxLength(300);

        builder.Property(p => p.LogoContentType)
            .HasMaxLength(100);

        builder.Property(p => p.UpdatedAt)
            .IsRequired();
    }
}
