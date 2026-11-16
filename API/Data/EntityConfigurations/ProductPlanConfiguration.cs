using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Api.Entities;

namespace Platform.Api.Data.EntityConfigurations;

public class ProductPlanConfiguration : IEntityTypeConfiguration<ProductPlan>
{
    public void Configure(EntityTypeBuilder<ProductPlan> builder)
    {
        builder.ToTable("ProductPlans");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .HasMaxLength(26)
            .IsRequired();

        builder.Property(p => p.ServiceProductId)
            .HasMaxLength(26)
            .IsRequired();

        builder.Property(p => p.Code)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(p => p.DisplayName)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(p => p.IsActive).HasDefaultValue(true);

        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();

        // A code is unique within its product (the same code may exist on another product).
        builder.HasIndex(p => new { p.ServiceProductId, p.Code })
            .IsUnique();
    }
}
