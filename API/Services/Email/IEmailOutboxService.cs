using Platform.Api.Entities;
using Platform.Shared.Dtos.Email;
using Platform.Shared.Enums;

namespace Platform.Api.Services.Email;

public interface IEmailOutboxService
{
    EmailOutboxMessage Enqueue(
        EmailDeliveryKind kind,
        string toEmail,
        string subject,
        string htmlBody,
        string? customerId = null,
        string? licenseId = null,
        string? invoiceId = null,
        string? receiptId = null,
        byte[]? encryptedPayload = null);

    Task<IReadOnlyList<EmailDeliveryDto>> ListAsync(
        string? customerId = null,
        string? licenseId = null,
        string? invoiceId = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<EmailDeliveryDto> RetryAsync(string id, CancellationToken cancellationToken = default);
}
