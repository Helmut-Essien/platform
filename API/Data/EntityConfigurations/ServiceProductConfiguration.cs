using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Api.Entities;

namespace Platform.Api.Data.EntityConfigurations;

public class ServiceProductConfiguration : IEntityTypeConfiguration<ServiceProduct>
{
    public void Configure(EntityTypeBuilder<ServiceProduct> builder)
    {
        builder.ToTable("ServiceProducts");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .HasMaxLength(26)
            .IsRequired();

        builder.Property(s => s.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(s => s.Code)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(s => s.Description)
            .HasMaxLength(2000);

        builder.HasIndex(s => s.Code)
            .IsUnique();
    }
}
