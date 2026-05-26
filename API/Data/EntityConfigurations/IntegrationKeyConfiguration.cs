using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Api.Entities;

namespace Platform.Api.Data.EntityConfigurations;

public class IntegrationKeyConfiguration : IEntityTypeConfiguration<IntegrationKey>
{
    public void Configure(EntityTypeBuilder<IntegrationKey> builder)
    {
        builder.ToTable("IntegrationKeys");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.Id)
            .HasMaxLength(26)
            .IsRequired();

        builder.Property(i => i.ServiceProductId)
            .HasMaxLength(26)
            .IsRequired();

        builder.Property(i => i.KeyHash)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(i => i.KeyLookupHash)
            .HasMaxLength(64);

        builder.HasIndex(i => i.KeyLookupHash);

        builder.Property(i => i.CreatedAt)
            .IsRequired();

        builder.HasIndex(i => i.ServiceProductId);

        builder.HasIndex(i => i.ServiceProductId)
            .IsUnique()
            .HasFilter("\"IsActive\" = true");

        builder.HasOne(i => i.ServiceProduct)
            .WithMany(s => s.IntegrationKeys)
            .HasForeignKey(i => i.ServiceProductId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
