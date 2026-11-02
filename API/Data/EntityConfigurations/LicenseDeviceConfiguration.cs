using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Api.Entities;

namespace Platform.Api.Data.EntityConfigurations;

public class LicenseDeviceConfiguration : IEntityTypeConfiguration<LicenseDevice>
{
    public void Configure(EntityTypeBuilder<LicenseDevice> builder)
    {
        builder.ToTable("LicenseDevices");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).HasMaxLength(26).IsRequired();
        builder.Property(d => d.LicenseId).HasMaxLength(26).IsRequired();
        builder.Property(d => d.DeviceIdHash).HasMaxLength(64).IsRequired();
        builder.Property(d => d.Name).HasMaxLength(100);
        builder.Property(d => d.FirstSeenAt).IsRequired();
        builder.Property(d => d.LastSeenAt).IsRequired();

        // One row per device per license; also serves "devices of a license" (LicenseId prefix).
        builder.HasIndex(d => new { d.LicenseId, d.DeviceIdHash }).IsUnique();

        builder.HasOne(d => d.License)
            .WithMany(l => l.Devices)
            .HasForeignKey(d => d.LicenseId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
