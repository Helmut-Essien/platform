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
    ILogger<EmailOutboxWorker> logger,
    EmailOutboxSignal? signal = null,
    TimeProvider? clock = null) : BackgroundService
{
    /// <summary>A "Sending" row untouched this long is treated as abandoned (covers a full batch on a slow provider).</summary>
    internal static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(10);

    /// <summary>How long after start the shorter <see cref="EmailOutboxSettings.StartupReclaimSeconds"/> lease applies.</summary>
    internal static readonly TimeSpan StartupWindow = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly DateTime _startedAt = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Lease after which a "Sending" row may be reclaimed. Normally <see cref="LeaseTimeout"/>. Right after start,
    /// shorter: rows left by a process that stopped mid-send (IIS idle shutdown, recycle, deploy) come back within
    /// about two minutes. Still safe when an old instance overlaps during a recycle: its current send finishes
    /// within the send timeout (the lease is at least 4× that), and rows it claimed but hasn't started are skipped
    /// by it once reclaimed here (SendAsync and <see cref="StartAttemptAsync"/> check the claim stamp), so nothing is
    /// sent twice.
    /// </summary>
    internal TimeSpan CurrentLease(DateTime now)
    {
        if (now - _startedAt >= StartupWindow)
            return LeaseTimeout;
        var options = settings.Value;
        var seconds = Math.Max(options.Outbox.StartupReclaimSeconds, 4 * options.EffectiveTimeoutSeconds);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 30, 600));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(2, settings.Value.Outbox.PollIntervalSeconds));
        var batchSize = Math.Clamp(settings.Value.Outbox.BatchSize, 1, 100);
        List<string>? inFlightBatch = null;
        DateTime? inFlightClaimedAt = null;
        var lastPurge = DateTime.MinValue;
        await WarmBrandNameAsync(stoppingToken);

        while (true)
        {
            if (stoppingToken.IsCancellationRequested && inFlightBatch is null)
                break;

            // A full batch means more may be waiting: claim again straight away instead of after the poll delay.
            var moreWaiting = false;
            try
            {
                if (inFlightBatch is null && !stoppingToken.IsCancellationRequested)
                {
                    var claim = await ClaimAsync(stoppingToken);
                    inFlightBatch = claim.Ids;
                    inFlightClaimedAt = claim.ClaimedAt;
                    moreWaiting = inFlightBatch.Count >= batchSize;
                }

                if (inFlightBatch is { Count: > 0 })
                {
                    while (inFlightBatch.Count > 0)
                    {
                        var id = inFlightBatch[0];
                        inFlightBatch.RemoveAt(0);
                        var sendToken = stoppingToken.IsCancellationRequested
                            ? CancellationToken.None
                            : stoppingToken;
                        await SendAsync(id, inFlightClaimedAt, sendToken);
                    }
                }

                // Always clear, including after an empty claim: a non-null empty list would skip every later claim
                // and leave new emails waiting until the process restarts.
                inFlightBatch = null;

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
                moreWaiting = false; // back off to the poll delay while something is failing
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

            if (moreWaiting)
                continue;
            // Sleep until the poll interval passes or a committed email wakes us (EmailOutboxWakeInterceptor).
            if (signal is not null)
                await signal.WaitAsync(delay, stoppingToken);
            else
                await Task.Delay(delay, stoppingToken);
        }
    }

    /// <summary>The ids claimed and the claim time written to their UpdatedAt (this worker's lease token).</summary>
    internal sealed record ClaimedBatch(List<string> Ids, DateTime ClaimedAt);

    internal async Task<List<string>> ClaimBatchAsync(CancellationToken cancellationToken) =>
        (await ClaimAsync(cancellationToken)).Ids;

    internal async Task<ClaimedBatch> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Captured across strategy retries: after a successful commit, a transient error can
        // still trigger a retry — return the same ids instead of re-querying an empty Pending set.
        ClaimedBatch? committedClaim = null;
        List<string>? pendingClaimIds = null;
        var pendingClaimAt = DateTime.MinValue;

        // EnableRetryOnFailure requires user transactions to run inside CreateExecutionStrategy.
        return await RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
            if (committedClaim is not null)
                return committedClaim;

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
                    committedClaim = new ClaimedBatch(confirmed, pendingClaimAt);
                    return committedClaim;
                }

                pendingClaimIds = null;
            }

            var now = UtcNow;
            var stale = now - CurrentLease(now);
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
            pendingClaimAt = now;
            await transaction.CommitAsync(ct);
            committedClaim = new ClaimedBatch(pendingClaimIds, now);
            return committedClaim;
        }, cancellationToken);
    }

    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);

    // Backoff doubles from RetryBaseSeconds (30 s, 1 min, 2 min, …) and is capped so a provider outage of several
    // hours does not exhaust the attempts of every queued message (B3).
    internal static TimeSpan ComputeBackoff(int attemptCount, int baseSeconds = 30)
    {
        var first = Math.Clamp(baseSeconds, 5, 3600);
        var exponent = Math.Min(Math.Max(1, attemptCount) - 1, 30); // 2^30 × 5 s is far past the cap
        return TimeSpan.FromSeconds(Math.Min(first * Math.Pow(2, exponent), MaxBackoff.TotalSeconds));
    }

    internal Task SendAsync(string id, CancellationToken cancellationToken) => SendAsync(id, null, cancellationToken);

    /// <param name="claimedAt">
    /// The claim time this worker wrote (its lease token). When the row's UpdatedAt no longer matches, another
    /// worker reclaimed it while it waited in this batch (e.g. an overlapping instance after a recycle) and owns it
    /// now: skip it, or the email would go out twice.
    /// </param>
    internal async Task SendAsync(string id, DateTime? claimedAt, CancellationToken cancellationToken)
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
        if (claimedAt is { } lease && (message.UpdatedAt - lease).Duration() > TimeSpan.FromMilliseconds(1))
            return; // tolerance: Postgres stores microseconds, .NET ticks are 100 ns

        // Poison guard: a message whose earlier attempts crashed the worker (or hung past the stale window) is
        // reclaimed with its attempts already counted. Stop once the limit is reached instead of looping forever.
        var maxAttempts = Math.Max(1, settings.Value.Outbox.MaxAttempts);
        if (message.AttemptCount >= maxAttempts)
        {
            message.Status = EmailDeliveryStatus.DeadLetter;
            message.NextAttemptAt = null;
            message.LastError ??= "Delivery did not complete after the maximum number of attempts.";
            message.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogError("Email delivery {DeliveryId} dead-lettered after {Attempts} unfinished attempts.", id, message.AttemptCount);
            return;
        }

        // Count the attempt durably BEFORE sending, and only if the claim is still ours: if a long batch let
        // another worker reclaim this row as stale, its UpdatedAt changed and we skip it (no duplicate email).
        if (!await StartAttemptAsync(db, message, cancellationToken))
            return;

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
            // Brand profile: one small row, read per send so Settings changes apply to the next email.
            var profile = await invoiceBrand.GetProfileEntityAsync(cancellationToken);
            var paymentOptions = InvoiceBrandService.DeserializePaymentOptions(profile.PaymentOptionsJson)
                .Select(x => new InvoicePaymentOption(x.Method, x.Details))
                .ToList();
            body = EmailTemplateService.ApplyBrand(body, EmailBrandContact.From(profile), paymentOptions.Count > 0);
            InvoiceLetterhead Letterhead() => new(
                profile.CompanyName, profile.AddressLine1, profile.AddressLine2,
                profile.Phone, profile.Website, profile.LogoBytes, paymentOptions,
                profile.ContactEmail, profile.TaxId);

            if (message.Kind == EmailDeliveryKind.Invoice && message.InvoiceId is not null)
            {
                var invoice = await db.Invoices
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Include(x => x.Customer)
                    .Include(x => x.ServiceProduct)
                    .Include(x => x.Receipts)
                    .AsSplitQuery()
                    .FirstOrDefaultAsync(x => x.Id == message.InvoiceId, cancellationToken)
                    ?? throw new InvalidOperationException("Invoice for email delivery no longer exists.");

                // E4: an invoice voided after it was queued must not be sent to the customer.
                if (invoice.Status == InvoiceStatus.Void)
                {
                    await CancelAsync(db, message, "Invoice was voided before the email was sent.", cancellationToken);
                    return;
                }

                var payUrl = scope.ServiceProvider.GetService<Platform.Api.Services.Renewals.PayLinkService>()
                    ?.BuildUrl(invoice, DateTime.UtcNow);
                var pdf = pdfGenerator.Generate(invoice, invoice.Customer, Letterhead(), payUrl);
                attachments = [new EmailAttachment($"{invoice.InvoiceNumber}.pdf", "application/pdf", pdf)];
            }
            else if (message.Kind == EmailDeliveryKind.PaymentReceipt && message.ReceiptId is not null)
            {
                var receiptPdf = scope.ServiceProvider.GetService<IReceiptPdfGenerator>();
                // Two no-tracking reads: Receipt -> Invoice -> Receipts would be an include cycle.
                var receipt = await db.Receipts
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Id == message.ReceiptId, cancellationToken);
                // A payment refunded/reversed before its receipt email went out: don't thank the customer for it.
                if (receipt is { Status: ReceiptStatus.Reversed })
                {
                    await CancelAsync(db, message, "Receipt was reversed before the email was sent.", cancellationToken);
                    return;
                }
                var receiptInvoice = receipt is null || receiptPdf is null
                    ? null
                    : await db.Invoices
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .Include(i => i.Customer)
                        .Include(i => i.Receipts)
                        .AsSplitQuery()
                        .FirstOrDefaultAsync(i => i.Id == receipt.InvoiceId, cancellationToken);
                if (receipt is not null && receiptInvoice is not null)
                {
                    var pdf = receiptPdf!.GenerateReceipt(receipt, receiptInvoice, receiptInvoice.Customer, Letterhead());
                    attachments = [new EmailAttachment($"{receipt.ReceiptNumber}.pdf", "application/pdf", pdf)];
                }
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

    private static async Task<bool> StartAttemptAsync(
        AppDbContext db,
        Platform.Api.Entities.EmailOutboxMessage message,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (db.Database.IsRelational())
        {
            // Tolerance covers Postgres' microsecond precision vs .NET ticks; a reclaim moves UpdatedAt by minutes.
            var claimedFrom = message.UpdatedAt.AddMilliseconds(-1);
            var claimedTo = message.UpdatedAt.AddMilliseconds(1);
            var rows = await db.EmailOutboxMessages
                .Where(m => m.Id == message.Id && m.Status == EmailDeliveryStatus.Sending
                    && m.UpdatedAt >= claimedFrom && m.UpdatedAt <= claimedTo)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.AttemptCount, m => m.AttemptCount + 1)
                    .SetProperty(m => m.UpdatedAt, now), cancellationToken);
            if (rows == 0)
                return false;
            var entry = db.Entry(message);
            message.AttemptCount++;
            message.UpdatedAt = now;
            entry.Property(m => m.AttemptCount).OriginalValue = message.AttemptCount;
            entry.Property(m => m.UpdatedAt).OriginalValue = now;
            entry.Property(m => m.AttemptCount).IsModified = false;
            entry.Property(m => m.UpdatedAt).IsModified = false;
            return true;
        }

        message.AttemptCount++;
        message.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
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
            : DateTime.UtcNow.Add(ComputeBackoff(message.AttemptCount, settings.Value.Outbox.RetryBaseSeconds));
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

    /// <summary>Loads the brand company name once at startup (read-only), so emails queued before the first send use it.</summary>
    private async Task WarmBrandNameAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var cache = scope.ServiceProvider.GetService<BrandNameCache>();
            if (cache is null)
                return;
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var profile = await db.InvoiceBrandProfiles.AsNoTracking()
                .OrderByDescending(p => p.UpdatedAt)
                .ThenBy(p => p.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (profile is not null)
                cache.Set(profile);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load the brand name at startup; emails use the default until the profile is read.");
        }
    }
}
