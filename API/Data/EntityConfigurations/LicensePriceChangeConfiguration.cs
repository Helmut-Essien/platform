using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Api.Entities;

namespace Platform.Api.Data.EntityConfigurations;

public class LicensePriceChangeConfiguration : IEntityTypeConfiguration<LicensePriceChange>
{
    public void Configure(EntityTypeBuilder<LicensePriceChange> builder)
    {
        builder.ToTable("LicensePriceChanges");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasMaxLength(26).IsRequired();
        builder.Property(c => c.LicenseId).HasMaxLength(26).IsRequired();
        builder.Property(c => c.BatchId).HasMaxLength(26).IsRequired();
        builder.Property(c => c.PlanCode).HasMaxLength(50).IsRequired();
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(c => c.EffectiveFrom).IsRequired();
        builder.Property(c => c.OldAmount).HasPrecision(18, 2);
        builder.Property(c => c.OldTaxAmount).HasPrecision(18, 2);
        builder.Property(c => c.OldCurrency).HasMaxLength(3);
        builder.Property(c => c.NewAmount).HasPrecision(18, 2);
        builder.Property(c => c.NewTaxAmount).HasPrecision(18, 2);
        builder.Property(c => c.NewCurrency).HasMaxLength(3).IsRequired();
        builder.Property(c => c.CreatedBy).HasMaxLength(256);
        builder.Property(c => c.CancelledBy).HasMaxLength(256);
        builder.Property(c => c.CancelReason).HasMaxLength(200);
        builder.Property(c => c.AppliedInvoiceId).HasMaxLength(26);
        builder.Property(c => c.Version).IsConcurrencyToken().HasColumnType("bigint");

        // At most one pending change per license; also serves "the pending change of these licenses".
        builder.HasIndex(c => c.LicenseId)
            .IsUnique()
            .HasFilter("\"Status\" = 'Pending'")
            .HasDatabaseName("IX_LicensePriceChanges_OnePendingPerLicense");

        // History of a license, newest first.
        builder.HasIndex(c => new { c.LicenseId, c.CreatedAt });

        // Notice worker: pending changes whose notice hasn't been queued, by effective date.
        builder.HasIndex(c => c.EffectiveFrom)
            .HasFilter("\"Status\" = 'Pending' AND \"NoticeQueuedAt\" IS NULL")
            .HasDatabaseName("IX_LicensePriceChanges_NoticeDue");

        builder.HasOne(c => c.License)
            .WithMany()
            .HasForeignKey(c => c.LicenseId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
