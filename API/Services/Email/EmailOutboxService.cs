using Microsoft.EntityFrameworkCore;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Shared.Dtos.Email;
using Platform.Shared.Enums;

namespace Platform.Api.Services.Email;

public class EmailOutboxService(AppDbContext db) : IEmailOutboxService
{
    public EmailOutboxMessage Enqueue(
        EmailDeliveryKind kind,
        string toEmail,
        string subject,
        string htmlBody,
        string? customerId = null,
        string? licenseId = null,
        string? invoiceId = null,
        string? receiptId = null,
        byte[]? encryptedPayload = null)
    {
        var message = new EmailOutboxMessage
        {
            Kind = kind,
            ToEmail = toEmail.Trim().ToLowerInvariant(),
            Subject = subject,
            HtmlBody = htmlBody,
            CustomerId = customerId,
            LicenseId = licenseId,
            InvoiceId = invoiceId,
            ReceiptId = receiptId,
            EncryptedPayload = encryptedPayload,
            NextAttemptAt = DateTime.UtcNow
        };
        db.EmailOutboxMessages.Add(message);
        return message;
    }

    public async Task<IReadOnlyList<EmailDeliveryDto>> ListAsync(
        string? customerId = null,
        string? licenseId = null,
        string? invoiceId = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var query = db.EmailOutboxMessages.AsNoTracking().AsQueryable();
        if (customerId is not null)
            query = query.Where(x => x.CustomerId == customerId);
        if (licenseId is not null)
            query = query.Where(x => x.LicenseId == licenseId);
        if (invoiceId is not null)
            query = query.Where(x => x.InvoiceId == invoiceId);

        var messages = await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(Math.Clamp(limit, 1, 250))
            .ToListAsync(cancellationToken);
        return messages.Select(Map).ToList();
    }

    public async Task<EmailDeliveryDto> RetryAsync(string id, CancellationToken cancellationToken = default)
    {
        var message = await db.EmailOutboxMessages.FindAsync([id], cancellationToken)
            ?? throw new InvalidOperationException("Email delivery not found.");

        if (message.Status is not (EmailDeliveryStatus.Failed or EmailDeliveryStatus.DeadLetter))
            throw new InvalidOperationException("Only failed email deliveries can be retried.");

        message.Status = EmailDeliveryStatus.Pending;
        message.NextAttemptAt = DateTime.UtcNow;
        message.LastError = null;
        message.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Map(message);
    }

    private static EmailDeliveryDto Map(EmailOutboxMessage x) => new()
    {
        Id = x.Id,
        Kind = x.Kind,
        Status = x.Status,
        ToEmail = x.ToEmail,
        Subject = x.Subject,
        CustomerId = x.CustomerId,
        LicenseId = x.LicenseId,
        InvoiceId = x.InvoiceId,
        ReceiptId = x.ReceiptId,
        AttemptCount = x.AttemptCount,
        LastError = x.LastError,
        CreatedAt = x.CreatedAt,
        SentAt = x.SentAt,
        NextAttemptAt = x.NextAttemptAt
    };
}
