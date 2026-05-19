using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Api.Entities;
using Platform.Shared.Enums;

namespace Platform.Api.Data.EntityConfigurations;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .HasMaxLength(26)
            .IsRequired();

        builder.Property(a => a.CustomerId)
            .HasMaxLength(26);

        builder.Property(a => a.LicenseId)
            .HasMaxLength(26);

        builder.Property(a => a.Action)
            .HasConversion<string>()
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(a => a.PerformedBy)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(a => a.DetailsJson)
            .HasColumnType("jsonb");

        builder.Property(a => a.IpAddress)
            .HasMaxLength(45);

        builder.Property(a => a.Timestamp)
            .IsRequired();

        builder.HasIndex(a => a.Timestamp);

        builder.HasOne(a => a.Customer)
            .WithMany(c => c.AuditLogs)
            .HasForeignKey(a => a.CustomerId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(a => a.License)
            .WithMany(l => l.AuditLogs)
            .HasForeignKey(a => a.LicenseId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
