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
        var lastPurge = DateTime.MinValue;

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

            if (DateTime.UtcNow - lastPurge >= TimeSpan.FromHours(1))
            {
                lastPurge = DateTime.UtcNow;
                try
                {
                    await PurgeAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Email outbox retention purge failed.");
                }
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    internal async Task<List<string>> ClaimBatchAsync(CancellationToken cancellationToken)
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

    // Backoff doubles from 2 minutes and is capped so a provider outage of several hours
    // does not exhaust the attempts of every queued message (B3).
    internal static TimeSpan ComputeBackoff(int attemptCount) =>
        TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Math.Max(1, attemptCount)), 360));

    internal async Task SendAsync(string id, CancellationToken cancellationToken)
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

        // Only messages this worker claimed may be sent (a manual retry or another worker may have changed it).
        if (message.Status != EmailDeliveryStatus.Sending)
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

                // E4: an invoice voided after it was queued must not be sent to the customer.
                if (invoice.Status == InvoiceStatus.Void)
                {
                    await CancelAsync(db, message, "Invoice was voided before the email was sent.", cancellationToken);
                    return;
                }

                var profile = await invoiceBrand.GetProfileEntityAsync(cancellationToken);
                var paymentOptions = InvoiceBrandService.DeserializePaymentOptions(profile.PaymentOptionsJson)
                    .Select(x => new InvoicePaymentOption(x.Method, x.Details))
                    .ToList();
                var letterhead = new InvoiceLetterhead(
                    profile.CompanyName, profile.AddressLine1, profile.AddressLine2,
                    profile.Phone, profile.Website, profile.LogoBytes, paymentOptions);
                var payUrl = scope.ServiceProvider.GetService<Platform.Api.Services.Renewals.PayLinkService>()
                    ?.BuildUrl(invoice, DateTime.UtcNow);
                var pdf = pdfGenerator.Generate(invoice, invoice.Customer, letterhead, payUrl);
                attachments = [new EmailAttachment($"{invoice.InvoiceNumber}.pdf", "application/pdf", pdf)];
            }

            await sender.SendAsync(message.ToEmail, message.Subject, body, attachments, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordFailureAsync(db, message, ex, cancellationToken);
            return;
        }

        // The email has been handed to the provider. From here on a bookkeeping failure must NOT
        // be recorded as a send failure, or the customer would receive the email again (E2).
        await RecordSentAsync(db, message, cancellationToken);
    }

    private async Task CancelAsync(
        AppDbContext db,
        Platform.Api.Entities.EmailOutboxMessage message,
        string reason,
        CancellationToken cancellationToken)
    {
        message.Status = EmailDeliveryStatus.Cancelled;
        message.NextAttemptAt = null;
        message.LastError = reason;
        message.UpdatedAt = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to record cancellation of email delivery {DeliveryId}.", message.Id);
        }
    }

    private async Task RecordSentAsync(
        AppDbContext db,
        Platform.Api.Entities.EmailOutboxMessage message,
        CancellationToken cancellationToken)
    {
        var sentAt = DateTime.UtcNow;
        var id = message.Id;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                if (attempt > 1)
                {
                    db.ChangeTracker.Clear();
                    message = await db.EmailOutboxMessages.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                        ?? throw new InvalidOperationException("Outbox message disappeared.");
                    message.AttemptCount++;
                }

                message.Status = EmailDeliveryStatus.Sent;
                message.SentAt = sentAt;
                message.UpdatedAt = sentAt;
                message.NextAttemptAt = null;
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
                await MarkLicenseKeyDeliveredAsync(db, message, sentAt, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(
                    ex,
                    "Email delivery {DeliveryId} was sent but could not be marked Sent (attempt {Attempt}).",
                    id,
                    attempt);
            }
        }

        // Still unrecorded: the message stays in 'Sending' and is reclaimed as stale after 10 minutes,
        // which can produce one duplicate email. The database is unavailable, so there is nothing safer to do.
    }

    private async Task RecordFailureAsync(
        AppDbContext db,
        Platform.Api.Entities.EmailOutboxMessage message,
        Exception ex,
        CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, settings.Value.Outbox.MaxAttempts);
        message.Status = message.AttemptCount >= maxAttempts || ex is PermanentEmailFailureException
            ? EmailDeliveryStatus.DeadLetter
            : EmailDeliveryStatus.Failed;
        message.NextAttemptAt = message.Status == EmailDeliveryStatus.DeadLetter
            ? null
            : DateTime.UtcNow.Add(ComputeBackoff(message.AttemptCount));
        message.LastError = Sanitize(ex.Message);
        message.UpdatedAt = DateTime.UtcNow;
        // B3: the encrypted payload and placeholders are kept on DeadLetter so a provider outage
        // does not permanently destroy license-key / password-reset emails. They are purged by
        // PurgeAsync after Outbox.DeadLetterPayloadRetentionDays.
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

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning(ex, "Email delivery {DeliveryId} failed on attempt {Attempt}.", message.Id, message.AttemptCount);
        }
        catch (Exception saveEx) when (saveEx is not OperationCanceledException)
        {
            // E2: do not abort the rest of the claimed batch; the message is reclaimed as stale later.
            logger.LogError(saveEx, "Failed to record failure for email delivery {DeliveryId}.", message.Id);
        }
    }

    /// <summary>
    /// Retention (P12/B3): redacts sensitive payloads of dead-lettered messages after the retention
    /// window and deletes old Sent/Cancelled rows so the outbox table does not grow forever.
    /// </summary>
    private const int PurgeChunkSize = 500;
    private const int MaxPurgeChunks = 40;

    internal async Task PurgeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var outbox = settings.Value.Outbox;

        var payloadCutoff = now.AddDays(-Math.Max(1, outbox.DeadLetterPayloadRetentionDays));
        var staleDead = await db.EmailOutboxMessages
            .Where(m => m.Status == EmailDeliveryStatus.DeadLetter
                && m.UpdatedAt <= payloadCutoff
                && m.EncryptedPayload != null)
            .Take(200)
            .ToListAsync(cancellationToken);
        foreach (var message in staleDead)
        {
            message.EncryptedPayload = null;
            message.HtmlBody = message.HtmlBody
                .Replace("{{LICENSE_KEY}}", "[redacted]", StringComparison.Ordinal)
                .Replace("{{RESET_LINK}}", "[redacted]", StringComparison.Ordinal);
            message.UpdatedAt = now;
        }

        var retentionCutoff = now.AddDays(-Math.Max(7, outbox.SentRetentionDays));
        var deleted = 0;
        if (db.Database.IsRelational())
        {
            // Chunked set-based deletes: a large backlog is cleared in one pass without loading rows.
            for (var chunk = 0; chunk < MaxPurgeChunks; chunk++)
            {
                var ids = db.EmailOutboxMessages
                    .Where(m => (m.Status == EmailDeliveryStatus.Sent || m.Status == EmailDeliveryStatus.Cancelled)
                        && m.UpdatedAt <= retentionCutoff)
                    .OrderBy(m => m.UpdatedAt)
                    .Select(m => m.Id)
                    .Take(PurgeChunkSize);
                var n = await db.EmailOutboxMessages
                    .Where(m => ids.Contains(m.Id))
                    .ExecuteDeleteAsync(cancellationToken);
                deleted += n;
                if (n < PurgeChunkSize)
                    break;
            }
        }
        else
        {
            var old = await db.EmailOutboxMessages
                .Where(m => (m.Status == EmailDeliveryStatus.Sent || m.Status == EmailDeliveryStatus.Cancelled)
                    && m.UpdatedAt <= retentionCutoff)
                .OrderBy(m => m.UpdatedAt)
                .Take(PurgeChunkSize)
                .ToListAsync(cancellationToken);
            db.EmailOutboxMessages.RemoveRange(old);
            deleted = old.Count;
        }

        if (staleDead.Count > 0 || (deleted > 0 && !db.Database.IsRelational()))
            await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkLicenseKeyDeliveredAsync(
        AppDbContext db,
        Platform.Api.Entities.EmailOutboxMessage message,
        DateTime sentAt,
        CancellationToken cancellationToken)
    {
        if (message.LicenseId is null
            || message.Kind is not (EmailDeliveryKind.LicenseKey or EmailDeliveryKind.LicenseKeyRotated))
            return;

        try
        {
            // Separate from the outbox save so a license concurrency conflict cannot
            // mark a delivered message as failed and cause the key to be emailed again.
            await db.Licenses
                .IgnoreQueryFilters()
                .Where(l => l.Id == message.LicenseId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(l => l.LicenseKeySentAt, sentAt),
                    cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "License key email {DeliveryId} was sent, but LicenseKeySentAt was not updated for {LicenseId}.",
                message.Id,
                message.LicenseId);
        }
    }

    private static string Sanitize(string value) =>
        value.Length <= 2000 ? value : value[..2000];
}
