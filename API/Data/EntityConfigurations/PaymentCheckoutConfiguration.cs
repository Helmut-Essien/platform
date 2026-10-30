using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Api.Entities;

namespace Platform.Api.Data.EntityConfigurations;

public class PaymentCheckoutConfiguration : IEntityTypeConfiguration<PaymentCheckout>
{
    public void Configure(EntityTypeBuilder<PaymentCheckout> builder)
    {
        builder.ToTable("PaymentCheckouts", table =>
        {
            table.HasCheckConstraint("CK_PaymentCheckouts_AmountPositive", "\"Amount\" > 0 AND \"AmountMinor\" > 0");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasMaxLength(26).IsRequired();
        builder.Property(x => x.InvoiceId).HasMaxLength(26).IsRequired();
        builder.Property(x => x.Provider).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Reference).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Amount).HasPrecision(18, 2);
        builder.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.AuthorizationUrl).HasMaxLength(500);
        builder.Property(x => x.Channel).HasMaxLength(50);
        builder.Property(x => x.ProviderTransactionId).HasMaxLength(100);
        builder.Property(x => x.ReceiptId).HasMaxLength(26);
        builder.Property(x => x.LastError).HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken();

        builder.HasOne(x => x.Invoice)
            .WithMany()
            .HasForeignKey(x => x.InvoiceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.Reference).IsUnique();
        builder.HasIndex(x => new { x.InvoiceId, x.Status });
        builder.HasIndex(x => new { x.Status, x.CreatedAt });
    }
}
