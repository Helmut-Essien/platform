using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Api.Entities;
using Platform.Shared.Enums;

namespace Platform.Api.Data.EntityConfigurations;

public class PaymentTransactionConfiguration : IEntityTypeConfiguration<PaymentTransaction>
{
    public void Configure(EntityTypeBuilder<PaymentTransaction> builder)
    {
        builder.ToTable("PaymentTransactions", table =>
        {
            table.HasCheckConstraint("CK_PaymentTransactions_AmountPositive", "\"Amount\" > 0");
            table.HasCheckConstraint(
                "CK_PaymentTransactions_PaymentShape",
                """
                ("Kind" = 'Payment' AND "ReceiptId" IS NOT NULL AND "ReversesTransactionId" IS NULL)
                OR ("Kind" = 'Reversal' AND "ReceiptId" IS NULL AND "ReversesTransactionId" IS NOT NULL)
                """);
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id)
            .HasMaxLength(26)
            .IsRequired();

        builder.Property(x => x.InvoiceId)
            .HasMaxLength(26)
            .IsRequired();

        builder.Property(x => x.Kind)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.Amount)
            .HasPrecision(18, 2);

        builder.Property(x => x.PaymentMethod)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(x => x.PaymentReference)
            .HasMaxLength(200);

        builder.Property(x => x.Notes)
            .HasMaxLength(2000);

        builder.Property(x => x.ReceiptId)
            .HasMaxLength(26);

        builder.Property(x => x.ReversesTransactionId)
            .HasMaxLength(26);

        builder.Property(x => x.IdempotencyKey)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(x => x.PerformedBy)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(x => x.CreatedAt)
            .IsRequired();

        builder.HasIndex(x => x.InvoiceId);
        builder.HasIndex(x => new { x.InvoiceId, x.IdempotencyKey }).IsUnique();
        builder.HasIndex(x => x.ReceiptId).IsUnique();
        builder.HasIndex(x => x.ReversesTransactionId).IsUnique();

        builder.HasOne(x => x.Invoice)
            .WithMany(i => i.PaymentTransactions)
            .HasForeignKey(x => x.InvoiceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Receipt)
            .WithOne(r => r.PaymentTransaction)
            .HasForeignKey<PaymentTransaction>(x => x.ReceiptId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.ReversesTransaction)
            .WithMany()
            .HasForeignKey(x => x.ReversesTransactionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
