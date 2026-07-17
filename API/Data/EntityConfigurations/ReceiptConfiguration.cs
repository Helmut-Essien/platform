using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Api.Entities;

namespace Platform.Api.Data.EntityConfigurations;

public class ReceiptConfiguration : IEntityTypeConfiguration<Receipt>
{
    public void Configure(EntityTypeBuilder<Receipt> builder)
    {
        builder.ToTable("Receipts");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Id)
            .HasMaxLength(26)
            .IsRequired();

        builder.Property(r => r.InvoiceId)
            .HasMaxLength(26)
            .IsRequired();

        builder.Property(r => r.ReceiptNumber)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(r => r.AmountPaid)
            .HasPrecision(18, 2);

        builder.Property(r => r.PaymentMethod)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(r => r.PaymentReference)
            .HasMaxLength(200);

        builder.Property(r => r.Notes)
            .HasMaxLength(2000);

        builder.Property(r => r.Status)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(r => r.ReversalReason)
            .HasMaxLength(2000);

        builder.Property(r => r.PaidAt)
            .IsRequired();

        builder.Property(r => r.CreatedAt)
            .IsRequired();

        builder.HasIndex(r => r.ReceiptNumber)
            .IsUnique();

        builder.HasIndex(r => r.InvoiceId);
        builder.HasIndex(r => r.Status);

        builder.HasOne(r => r.Invoice)
            .WithMany(i => i.Receipts)
            .HasForeignKey(r => r.InvoiceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
