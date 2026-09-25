using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Helpers;
using Platform.Api.Services.Billing;
using Platform.Shared.Enums;

namespace Platform.Api.Services.Email;

public class EmailOutboxWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<EmailSettings> settings,
    ILogger<EmailOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(2, settings.Value.Outbox.PollIntervalSeconds));
        List<string>? inFlightBatch = null;

        while (true)
        {
            if (stoppingToken.IsCancellationRequested && inFlightBatch is null)
                break;

            try
            {
                if (inFlightBatch is null && !stoppingToken.IsCancellationRequested)
                    inFlightBatch = await ClaimBatchAsync(stoppingToken);

                if (inFlightBatch is { Count: > 0 })
                {
                    while (inFlightBatch.Count > 0)
                    {
                        var id = inFlightBatch[0];
                        inFlightBatch.RemoveAt(0);
                        var sendToken = stoppingToken.IsCancellationRequested
                            ? CancellationToken.None
                            : stoppingToken;
                        await SendAsync(id, sendToken);
                    }

                    inFlightBatch = null;
                }

                if (stoppingToken.IsCancellationRequested)
                    break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Email outbox poll failed.");
                inFlightBatch = null;
            }

            if (stoppingToken.IsCancellationRequested)
                break;

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task<List<string>> ClaimBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Captured across strategy retries: after a successful commit, a transient error can
        // still trigger a retry — return the same ids instead of re-querying an empty Pending set.
        List<string>? committedClaimIds = null;
        List<string>? pendingClaimIds = null;

        // EnableRetryOnFailure requires user transactions to run inside CreateExecutionStrategy.
        return await RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
            if (committedClaimIds is not null)
                return committedClaimIds;

            if (transaction is null)
                throw new InvalidOperationException("Email outbox claim requires a relational transaction.");

            // Ambiguous commit: previous attempt may have committed then failed. Confirm by id.
            if (pendingClaimIds is { Count: > 0 })
            {
                var confirmed = await db.EmailOutboxMessages
                    .AsNoTracking()
                    .Where(m => pendingClaimIds.Contains(m.Id) && m.Status == EmailDeliveryStatus.Sending)
                    .Select(m => m.Id)
                    .ToListAsync(ct);
                if (confirmed.Count > 0)
                {
                    committedClaimIds = confirmed;
                    return confirmed;
                }

                pendingClaimIds = null;
            }

            var now = DateTime.UtcNow;
            var stale = now.AddMinutes(-10);
            var batchSize = Math.Clamp(settings.Value.Outbox.BatchSize, 1, 100);

            var messages = await db.EmailOutboxMessages
                .FromSqlInterpolated($"""
                    SELECT * FROM "EmailOutboxMessages"
                    WHERE (
                        ("Status" IN ('Pending', 'Failed') AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= {now}))
                        OR ("Status" = 'Sending' AND "UpdatedAt" <= {stale})
                    )
                    ORDER BY "CreatedAt"
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(ct);

            foreach (var message in messages)
            {
                message.Status = EmailDeliveryStatus.Sending;
                message.UpdatedAt = now;
            }

            await db.SaveChangesAsync(ct);
            pendingClaimIds = messages.Select(x => x.Id).ToList();
            await transaction.CommitAsync(ct);
            committedClaimIds = pendingClaimIds;
            return committedClaimIds;
        }, cancellationToken);
    }

    private async Task SendAsync(string id, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var protector = scope.ServiceProvider.GetRequiredService<EmailPayloadProtector>();
        var pdfGenerator = scope.ServiceProvider.GetRequiredService<IInvoicePdfGenerator>();
        var invoiceBrand = scope.ServiceProvider.GetRequiredService<IInvoiceBrandService>();
        var message = await db.EmailOutboxMessages.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (message is null)
            return;

        message.AttemptCount++;
        try
        {
            var body = message.HtmlBody;
            if (message.EncryptedPayload is { Length: > 0 })
            {
                var secret = protector.Unprotect(message.EncryptedPayload);
                body = body
                    .Replace("{{LICENSE_KEY}}", WebUtility.HtmlEncode(secret), StringComparison.Ordinal)
                    .Replace("{{RESET_LINK}}", WebUtility.HtmlEncode(secret), StringComparison.Ordinal);
            }

            IReadOnlyList<EmailAttachment>? attachments = null;
            if (message.Kind == EmailDeliveryKind.Invoice && message.InvoiceId is not null)
            {
                var invoice = await db.Invoices
                    .IgnoreQueryFilters()
                    .Include(x => x.Customer)
                    .Include(x => x.ServiceProduct)
                    .FirstOrDefaultAsync(x => x.Id == message.InvoiceId, cancellationToken)
                    ?? throw new InvalidOperationException("Invoice for email delivery no longer exists.");
                var profile = await invoiceBrand.GetProfileEntityAsync(cancellationToken);
                var paymentOptions = InvoiceBrandService.DeserializePaymentOptions(profile.PaymentOptionsJson)
                    .Select(x => new InvoicePaymentOption(x.Method, x.Details))
                    .ToList();
                var letterhead = new InvoiceLetterhead(
                    profile.CompanyName, profile.AddressLine1, profile.AddressLine2,
                    profile.Phone, profile.Website, profile.LogoBytes, paymentOptions);
                var pdf = pdfGenerator.Generate(invoice, invoice.Customer, letterhead);
                attachments = [new EmailAttachment($"{invoice.InvoiceNumber}.pdf", "application/pdf", pdf)];
            }

            await sender.SendAsync(message.ToEmail, message.Subject, body, attachments, cancellationToken);
            message.Status = EmailDeliveryStatus.Sent;
            message.SentAt = DateTime.UtcNow;
            message.UpdatedAt = DateTime.UtcNow;
            message.LastError = null;
            message.EncryptedPayload = null;
            message.HtmlBody = message.HtmlBody
                .Replace("{{LICENSE_KEY}}", "[delivered]", StringComparison.Ordinal)
                .Replace("{{RESET_LINK}}", "[delivered]", StringComparison.Ordinal);
            db.AuditLogs.Add(new Platform.Api.Entities.AuditLog
            {
                Action = AuditAction.EmailDeliverySent,
                PerformedBy = "system:email-outbox",
                CustomerId = message.CustomerId,
                LicenseId = message.LicenseId,
                InvoiceId = message.InvoiceId,
                DetailsJson = AuditJson.Serialize(new { deliveryId = message.Id, kind = message.Kind.ToString() })
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var maxAttempts = Math.Max(1, settings.Value.Outbox.MaxAttempts);
            message.Status = message.AttemptCount >= maxAttempts
                ? EmailDeliveryStatus.DeadLetter
                : EmailDeliveryStatus.Failed;
            message.NextAttemptAt = message.Status == EmailDeliveryStatus.DeadLetter
                ? null
                : DateTime.UtcNow.AddMinutes(Math.Pow(2, message.AttemptCount));
            message.LastError = Sanitize(ex.Message);
            message.UpdatedAt = DateTime.UtcNow;
            if (message.Status == EmailDeliveryStatus.DeadLetter)
            {
                message.EncryptedPayload = null;
                message.HtmlBody = message.HtmlBody
                    .Replace("{{LICENSE_KEY}}", "[redacted]", StringComparison.Ordinal)
                    .Replace("{{RESET_LINK}}", "[redacted]", StringComparison.Ordinal);
            }
            db.AuditLogs.Add(new Platform.Api.Entities.AuditLog
            {
                Action = AuditAction.EmailDeliveryFailed,
                PerformedBy = "system:email-outbox",
                CustomerId = message.CustomerId,
                LicenseId = message.LicenseId,
                InvoiceId = message.InvoiceId,
                DetailsJson = AuditJson.Serialize(new
                {
                    deliveryId = message.Id,
                    kind = message.Kind.ToString(),
                    attempt = message.AttemptCount
                })
            });
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning(ex, "Email delivery {DeliveryId} failed on attempt {Attempt}.", id, message.AttemptCount);
        }
    }

    private static string Sanitize(string value) =>
        value.Length <= 2000 ? value : value[..2000];
}
