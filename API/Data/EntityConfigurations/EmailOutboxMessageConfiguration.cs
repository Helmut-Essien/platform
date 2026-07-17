using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Platform.Api.Entities;

namespace Platform.Api.Data.EntityConfigurations;

public class EmailOutboxMessageConfiguration : IEntityTypeConfiguration<EmailOutboxMessage>
{
    public void Configure(EntityTypeBuilder<EmailOutboxMessage> builder)
    {
        builder.ToTable("EmailOutboxMessages");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasMaxLength(26).IsRequired();
        builder.Property(x => x.Kind).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(x => x.ToEmail).HasMaxLength(320).IsRequired();
        builder.Property(x => x.Subject).HasMaxLength(500).IsRequired();
        builder.Property(x => x.HtmlBody).IsRequired();
        builder.Property(x => x.CustomerId).HasMaxLength(26);
        builder.Property(x => x.LicenseId).HasMaxLength(26);
        builder.Property(x => x.InvoiceId).HasMaxLength(26);
        builder.Property(x => x.ReceiptId).HasMaxLength(26);
        builder.Property(x => x.LastError).HasMaxLength(2000);
        builder.Property(x => x.ProviderMessageId).HasMaxLength(500);

        builder.HasIndex(x => new { x.Status, x.NextAttemptAt });
        builder.HasIndex(x => x.CustomerId);
        builder.HasIndex(x => x.LicenseId);
        builder.HasIndex(x => x.InvoiceId);

        builder.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(x => x.License).WithMany().HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne(x => x.Receipt).WithMany().HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.SetNull);
    }
}
