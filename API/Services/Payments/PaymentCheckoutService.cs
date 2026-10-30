using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services.Email;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Dtos.Payments;
using Platform.Shared.Enums;

namespace Platform.Api.Services.Payments;

public interface IPaymentCheckoutService
{
    Task<OnlinePaymentsStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<CheckoutSessionDto> StartCheckoutAsync(string invoiceId, string performedBy, CancellationToken cancellationToken = default);

    Task<CheckoutStatusDto> GetCheckoutStatusAsync(string invoiceId, string? reference, CancellationToken cancellationToken = default);

    /// <summary>Handles a webhook whose signature was already validated. Never throws for bad payloads.</summary>
    Task HandleWebhookAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default);

    /// <summary>Re-verifies pending checkouts (missed webhooks) and expires stale ones. Returns how many were checked.</summary>
    Task<int> ReconcileAsync(CancellationToken cancellationToken = default);
}

/// <summary>Thrown when Paystack is not configured; mapped to 503.</summary>
public sealed class OnlinePaymentsDisabledException()
    : Exception("Online payments are not configured. Set Paystack__SecretKey to enable them.");

public sealed class PaymentCheckoutService(
    AppDbContext db,
    IBillingService billing,
    IPaymentGateway gateway,
    IOptions<PaystackOptions> paystackOptions,
    IOptions<AuthSettings> authOptions,
    ILogger<PaymentCheckoutService> logger) : IPaymentCheckoutService
{
    public const string SupportedCurrency = "GHS";
    public const string SystemActor = "system:paystack";
    public static readonly TimeSpan CheckoutLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan ReconcileMinAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OnDemandThrottle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SlowRecheckAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan SlowRecheckInterval = TimeSpan.FromMinutes(30);
    /// <summary>An open checkout is reused only while young, so the payer never lands on one about to expire.</summary>
    public static readonly TimeSpan CheckoutReuseWindow = TimeSpan.FromHours(1);
    private const int ReconcileBatchSize = 25;
    private const int MaxConsecutiveGatewayFailures = 3;
    private const int CheckoutLockClass = 0x50535431; // "PST1": advisory-lock namespace for per-invoice checkout creation

    private PaystackOptions Options => paystackOptions.Value;

    public async Task<OnlinePaymentsStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var options = Options;
        var dto = new OnlinePaymentsStatusDto
        {
            Enabled = options.Enabled,
            TestMode = options.Enabled && options.IsTestMode,
            CollectionCap = Math.Max(0, options.CollectionCap)
        };

        if (options.Enabled && dto.CollectionCap > 0)
        {
            // Gross money collected through Paystack in the current mode (test payments never count toward
            // the live cap). Reversing a receipt in the app does not refund Paystack, so it still counts.
            var live = !options.IsTestMode;
            var minor = await db.PaymentCheckouts
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(c => (c.Status == PaymentCheckoutStatus.Paid
                        || c.Status == PaymentCheckoutStatus.NeedsReview
                        || c.Status == PaymentCheckoutStatus.Refunded
                        || c.Status == PaymentCheckoutStatus.Disputed)
                    && c.LiveMode == live
                    && c.PaidAmountMinor != null)
                .SumAsync(c => c.PaidAmountMinor, cancellationToken) ?? 0L;
            dto.CollectedTotal = MinorUnits.ToAmount(minor);
        }

        return dto;
    }

    public async Task<CheckoutSessionDto> StartCheckoutAsync(
        string invoiceId,
        string performedBy,
        CancellationToken cancellationToken = default)
    {
        if (!Options.Enabled)
            throw new OnlinePaymentsDisabledException();

        // Only the customer is needed up front; the full invoice is read once, inside the lock.
        var customer = await db.Invoices.AsNoTracking()
            .Where(i => i.Id == invoiceId)
            .Select(i => i.Customer)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Invoice not found.");
        var email = CustomerContactResolver.Billing(customer)?.Trim();
        if (!IsValidEmail(email))
            throw new InvalidOperationException(
                "Paystack needs a valid billing or contact email for this customer. Add one on the customer page and try again.");

        return await RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
            // Serialises checkout creation per invoice (two tabs / double click get the same checkout) without
            // blocking receipts on the invoice row while Paystack is called.
            await LockCheckoutCreationAsync(invoiceId, ct);

            // Re-read the balance inside the lock so a payment recorded meanwhile is respected.
            var current = await billing.GetInvoiceAsync(invoiceId, ct) ?? throw new NotFoundException("Invoice not found.");
            EnsurePayable(current);
            var amount = decimal.Round(current.AmountDue, 2);
            var now = DateTime.UtcNow;

            var open = await db.PaymentCheckouts
                .IgnoreQueryFilters()
                .Where(c => c.InvoiceId == invoiceId && c.Status == PaymentCheckoutStatus.Pending)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync(ct);

            var reusable = open.FirstOrDefault(c =>
                c.Amount == amount
                && c.AuthorizationUrl is not null
                && now - c.CreatedAt >= TimeSpan.Zero
                && now - c.CreatedAt < CheckoutReuseWindow);
            if (reusable is not null)
            {
                if (transaction is not null)
                    await transaction.CommitAsync(ct);
                return ToSession(reusable, reused: true);
            }

            // Older open checkouts (other amount or too old to reuse) stay Pending: the payer may still be on
            // that Paystack page, so the reconcile worker keeps verifying them and expires them after 24 h.
            // If one is paid too, the overpayment is flagged NeedsReview rather than recorded.

            var reference = "PLT-" + NUlid.Ulid.NewUlid();
            var amountMinor = MinorUnits.FromAmount(amount);
            var baseUrl = authOptions.Value.ClientBaseUrl.TrimEnd('/');
            var invoicePath = $"{baseUrl}/invoices/{Uri.EscapeDataString(invoiceId)}";

            var session = await gateway.InitializeAsync(new GatewayCheckoutRequest(
                reference,
                amountMinor,
                SupportedCurrency,
                email,
                $"{invoicePath}?payment=return",
                new Dictionary<string, string>
                {
                    ["invoice_id"] = invoiceId,
                    ["invoice_number"] = current.InvoiceNumber,
                    ["cancel_action"] = $"{invoicePath}?payment=cancelled"
                }), ct);

            var checkout = new PaymentCheckout
            {
                InvoiceId = invoiceId,
                Reference = reference,
                Amount = amount,
                AmountMinor = amountMinor,
                Currency = SupportedCurrency,
                AuthorizationUrl = session.AuthorizationUrl,
                CreatedBy = performedBy,
                CreatedAt = now,
                LiveMode = !Options.IsTestMode
            };
            db.PaymentCheckouts.Add(checkout);
            await db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);

            logger.LogInformation(
                "Paystack checkout {Reference} created for invoice {InvoiceId} ({Amount} {Currency}).",
                reference, invoiceId, amount, SupportedCurrency);
            return ToSession(checkout, reused: false);
        }, cancellationToken);
    }

    public async Task<CheckoutStatusDto> GetCheckoutStatusAsync(
        string invoiceId,
        string? reference,
        CancellationToken cancellationToken = default)
    {
        if (!Options.Enabled)
            throw new OnlinePaymentsDisabledException();

        var query = db.PaymentCheckouts.IgnoreQueryFilters().Where(c => c.InvoiceId == invoiceId);
        if (!string.IsNullOrWhiteSpace(reference))
            query = query.Where(c => c.Reference == reference.Trim());

        var checkout = await query.OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("No online payment was started for this invoice.");

        if (checkout.Status == PaymentCheckoutStatus.Pending
            && (checkout.LastCheckedAt is null || DateTime.UtcNow - checkout.LastCheckedAt.Value >= OnDemandThrottle))
        {
            checkout = await ConfirmAsync(checkout, cancellationToken);
        }

        return ToStatus(checkout);
    }

    public async Task HandleWebhookAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        var evt = PaystackWebhookEvent.TryParse(body);
        if (evt is null)
        {
            logger.LogWarning("Paystack webhook body is not a valid event; ignored.");
            return;
        }

        // A test event must never touch live data and vice versa (shared URL or a key switch).
        if (evt.Domain is { } domain && !string.Equals(domain, Options.IsTestMode ? "test" : "live", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Paystack {Event} webhook for domain {Domain} ignored in {Mode} mode.",
                evt.Event, domain, Options.IsTestMode ? "test" : "live");
            return;
        }

        var kind = evt.Event switch
        {
            "charge.success" => WebhookKind.ChargeSuccess,
            "refund.processed" => WebhookKind.Refund,
            "charge.dispute.create" or "charge.dispute.remind" => WebhookKind.Dispute,
            _ => WebhookKind.Other
        };
        if (kind == WebhookKind.Other)
        {
            logger.LogInformation("Paystack webhook event {Event} ignored.", evt.Event);
            return;
        }

        if (string.IsNullOrWhiteSpace(evt.Reference) || evt.Reference.Length > 100)
        {
            logger.LogWarning("Paystack {Event} webhook without a usable reference; ignored.", evt.Event);
            return;
        }

        var checkout = await db.PaymentCheckouts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Reference == evt.Reference, cancellationToken);
        if (checkout is null)
        {
            // Not ours (e.g. a payment link or another integration on the same Paystack account).
            logger.LogInformation("Paystack {Event} webhook for unknown reference {Reference}; ignored.", evt.Event, evt.Reference);
            return;
        }

        if (kind == WebhookKind.ChargeSuccess)
        {
            // The payload is only a hint: ConfirmAsync re-verifies status, amount and currency with Paystack.
            // Replays and out-of-order deliveries are harmless (verify is authoritative, receipts are idempotent).
            await ConfirmAsync(checkout, cancellationToken);
            return;
        }

        await FlagAfterPaymentAsync(checkout, kind, evt, cancellationToken);
    }

    /// <summary>
    /// Refunds and disputes are flagged, not acted on: reversing the receipt (and any license impact) is an admin
    /// decision. Signed events are trusted for flagging only, since no money is recorded from them.
    /// </summary>
    private async Task FlagAfterPaymentAsync(PaymentCheckout checkout, WebhookKind kind, PaystackWebhookEvent evt, CancellationToken cancellationToken)
    {
        var amount = evt.AmountMinor is { } minor ? $" {MinorUnits.ToAmount(minor):0.00} {evt.Currency ?? checkout.Currency}" : string.Empty;
        string note;
        if (kind == WebhookKind.Refund)
        {
            checkout.Status = PaymentCheckoutStatus.Refunded;
            note = $"Refund{amount} processed at Paystack. Reverse the receipt if this was a full refund.";
        }
        else
        {
            if (checkout.Status == PaymentCheckoutStatus.Refunded)
                return; // a refund is the more final state
            checkout.Status = PaymentCheckoutStatus.Disputed;
            note = $"Chargeback/dispute{amount} opened at Paystack. Respond in the Paystack dashboard.";
        }

        checkout.LastError = Truncate(note);
        checkout.CompletedAt ??= DateTime.UtcNow;
        logger.LogWarning("Paystack checkout {Reference} on invoice {InvoiceId}: {Event}; flagged for review.",
            checkout.Reference, checkout.InvoiceId, evt.Event);
        await SaveCheckoutAsync(checkout, cancellationToken);
    }

    private enum WebhookKind { Other, ChargeSuccess, Refund, Dispute }

    public async Task<int> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (!Options.Enabled)
            return 0;

        var now = DateTime.UtcNow;
        var createdBefore = now - ReconcileMinAge;
        var checkedBefore = now - ReconcileMinAge;

        var candidates = await db.PaymentCheckouts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.Status == PaymentCheckoutStatus.Pending
                && c.CreatedAt < createdBefore
                && (c.LastCheckedAt == null || c.LastCheckedAt < checkedBefore))
            .OrderBy(c => c.LastCheckedAt ?? c.CreatedAt)
            .Select(c => new { c.Id, c.CreatedAt, c.LastCheckedAt })
            .Take(ReconcileBatchSize * 4)
            .ToListAsync(cancellationToken);

        // Old abandoned checkouts are re-checked less often (cheap: at most a few calls per hour each).
        var due = candidates
            .Where(c => now - c.CreatedAt >= CheckoutLifetime
                || now - c.CreatedAt < SlowRecheckAge
                || c.LastCheckedAt is null
                || now - c.LastCheckedAt.Value >= SlowRecheckInterval)
            .Take(ReconcileBatchSize)
            .ToList();

        var dueIds = due.Select(c => c.Id).ToList();
        var loaded = await db.PaymentCheckouts.IgnoreQueryFilters().AsNoTracking()
            .Where(c => dueIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        var processed = 0;
        var consecutiveFailures = 0;
        foreach (var item in due)
        {
            // Back off when Paystack is down or rate-limiting (429): the rest wait for the next run.
            if (consecutiveFailures >= MaxConsecutiveGatewayFailures)
            {
                logger.LogWarning("Paystack reconcile stopped after {Failures} failed verify calls; {Remaining} checkout(s) deferred.",
                    consecutiveFailures, due.Count - processed);
                break;
            }

            processed++;
            try
            {
                if (loaded.TryGetValue(item.Id, out var checkout))
                {
                    db.PaymentCheckouts.Attach(checkout);
                    LastGatewayCallFailed = false;
                    await ConfirmAsync(checkout, cancellationToken);
                    consecutiveFailures = LastGatewayCallFailed ? consecutiveFailures + 1 : 0;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Paystack reconcile failed for checkout {CheckoutId}.", item.Id);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        return processed;
    }

    /// <summary>
    /// Verifies the checkout with Paystack and applies the outcome. Gateway errors are recorded on the
    /// checkout (it stays Pending for the reconcile worker) instead of being thrown.
    /// </summary>
    /// <summary>True when the latest verify call failed (network, 429, 5xx); the worker uses it to back off.</summary>
    internal bool LastGatewayCallFailed { get; private set; }

    internal async Task<PaymentCheckout> ConfirmAsync(PaymentCheckout checkout, CancellationToken cancellationToken)
    {
        if (checkout.Status is PaymentCheckoutStatus.Paid or PaymentCheckoutStatus.NeedsReview
            or PaymentCheckoutStatus.Refunded or PaymentCheckoutStatus.Disputed)
            return checkout;

        // Created with the other key (test vs live): this key can't see it, so don't spend a verify call.
        if (checkout.LiveMode == Options.IsTestMode)
        {
            if (checkout.Status == PaymentCheckoutStatus.Pending)
            {
                checkout.Status = PaymentCheckoutStatus.Expired;
                checkout.CompletedAt = DateTime.UtcNow;
                checkout.LastError = $"Created in {(checkout.LiveMode ? "live" : "test")} mode; the active Paystack key is {(Options.IsTestMode ? "test" : "live")}.";
                return (await SaveCheckoutAsync(checkout, cancellationToken)).Current;
            }
            return checkout;
        }

        var now = DateTime.UtcNow;
        GatewayVerification verification;
        try
        {
            verification = await gateway.VerifyAsync(checkout.Reference, cancellationToken);
        }
        catch (PaymentGatewayException ex)
        {
            LastGatewayCallFailed = true;
            checkout.LastCheckedAt = now;
            checkout.LastError = Truncate(ex.Message);
            return (await SaveCheckoutAsync(checkout, cancellationToken)).Current;
        }

        LastGatewayCallFailed = false;
        checkout.LastCheckedAt = now;
        switch (verification.State)
        {
            case GatewayTransactionState.Success:
                return await ApplySuccessAsync(checkout, verification, cancellationToken);

            case GatewayTransactionState.Failed:
                if (checkout.Status == PaymentCheckoutStatus.Pending)
                {
                    checkout.Status = PaymentCheckoutStatus.Failed;
                    checkout.CompletedAt = now;
                }
                checkout.LastError = Truncate(verification.Message ?? "Payment failed.");
                break;

            default:
                if (checkout.Status == PaymentCheckoutStatus.Pending && now - checkout.CreatedAt >= CheckoutLifetime)
                {
                    checkout.Status = PaymentCheckoutStatus.Expired;
                    checkout.CompletedAt = now;
                    checkout.LastError = "Not paid within 24 hours.";
                }
                break;
        }

        return (await SaveCheckoutAsync(checkout, cancellationToken)).Current;
    }

    private async Task<PaymentCheckout> ApplySuccessAsync(
        PaymentCheckout checkout,
        GatewayVerification verification,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        checkout.Channel = Truncate(verification.Channel, 50);
        checkout.ProviderTransactionId = Truncate(verification.ProviderTransactionId, 100);
        // Missing or future paid_at (clock skew) falls back to now; billing rejects dates far in the future.
        checkout.PaidAt = verification.PaidAt is { } paid && paid <= now ? paid : now;
        checkout.PaidAmountMinor = verification.AmountMinor;

        // With fees passed to the payer, amount = requested_amount + fees; what must match is the requested amount.
        var expectedMatches = (verification.RequestedAmountMinor ?? verification.AmountMinor) == checkout.AmountMinor
            && verification.AmountMinor >= checkout.AmountMinor;
        if (!string.Equals(verification.Currency?.Trim(), checkout.Currency, StringComparison.OrdinalIgnoreCase)
            || !expectedMatches)
        {
            logger.LogWarning(
                "Paystack checkout {Reference}: paid {PaidMinor} {PaidCurrency} but expected {ExpectedMinor} {ExpectedCurrency}; flagged for review.",
                checkout.Reference, verification.AmountMinor, verification.Currency, checkout.AmountMinor, checkout.Currency);
            return await MarkNeedsReviewAsync(checkout,
                $"Paystack reported {MinorUnits.ToAmount(verification.AmountMinor):0.00} {verification.Currency}, expected {checkout.Amount:0.00} {checkout.Currency}.",
                cancellationToken);
        }

        // Persist the verification result first, so nothing is pending in the change tracker
        // when RecordReceiptAsync runs its own (retriable) transaction.
        var (saved, current) = await SaveCheckoutAsync(checkout, cancellationToken);
        if (!saved)
            return current; // another webhook/worker/status call is handling it
        var checkoutId = checkout.Id;

        ReceiptDto receipt;
        try
        {
            receipt = await billing.RecordReceiptAsync(checkout.InvoiceId, new RecordReceiptRequest
            {
                IdempotencyKey = $"paystack:{checkout.Reference}",
                AmountPaid = checkout.Amount,
                PaymentMethod = MapChannel(verification.Channel),
                PaymentReference = checkout.Reference,
                PaidAt = checkout.PaidAt,
                Notes = $"Paystack {verification.Channel ?? "payment"}"
                    + (verification.ProviderTransactionId is { Length: > 0 } id ? $", transaction {id}" : string.Empty)
            }, SystemActor, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ConflictException or NotFoundException)
        {
            // e.g. the invoice was voided or already settled another way: money is at Paystack but unapplied.
            logger.LogError(
                "Paystack checkout {Reference} was paid but could not be applied to invoice {InvoiceId}: {Error}",
                checkout.Reference, checkout.InvoiceId, ex.Message);
            db.ChangeTracker.Clear();
            var reloaded = await ReloadAsync(checkoutId, cancellationToken);
            return await MarkNeedsReviewAsync(reloaded, ex.Message, cancellationToken);
        }

        var fresh = await ReloadAsync(checkoutId, cancellationToken);
        fresh.Status = PaymentCheckoutStatus.Paid;
        fresh.ReceiptId = receipt.Id;
        fresh.CompletedAt ??= now;
        fresh.LastError = null;
        // The receipt is idempotent (paystack:{reference}); losing this race just means another caller marked it Paid.
        fresh = (await SaveCheckoutAsync(fresh, cancellationToken)).Current;
        logger.LogInformation(
            "Paystack checkout {Reference} applied to invoice {InvoiceId} as receipt {ReceiptNumber}.",
            fresh.Reference, fresh.InvoiceId, receipt.ReceiptNumber);
        return fresh;
    }

    private async Task<PaymentCheckout> MarkNeedsReviewAsync(PaymentCheckout checkout, string reason, CancellationToken cancellationToken)
    {
        checkout.Status = PaymentCheckoutStatus.NeedsReview;
        checkout.CompletedAt ??= DateTime.UtcNow;
        checkout.LastError = Truncate(reason);
        return (await SaveCheckoutAsync(checkout, cancellationToken)).Current;
    }

    /// <summary>
    /// Saves a checkout guarded by its concurrency token. If another caller changed it first, the stale
    /// changes are dropped and the current row is returned (saved = false).
    /// </summary>
    private async Task<(bool Saved, PaymentCheckout Current)> SaveCheckoutAsync(PaymentCheckout checkout, CancellationToken cancellationToken)
    {
        checkout.Version = Guid.NewGuid();
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return (true, checkout);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Paystack checkout {Reference} was updated concurrently; using the latest state.", checkout.Reference);
            db.ChangeTracker.Clear();
            return (false, await ReloadAsync(checkout.Id, cancellationToken));
        }
    }

    private static bool IsValidEmail([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254)
            return false;
        try
        {
            var parsed = new System.Net.Mail.MailAddress(email);
            return parsed.Address == email && parsed.Host.Contains('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<PaymentCheckout> ReloadAsync(string id, CancellationToken cancellationToken) =>
        await db.PaymentCheckouts.IgnoreQueryFilters().FirstAsync(c => c.Id == id, cancellationToken);

    public static PaymentMethod MapChannel(string? channel) => channel?.Trim().ToLowerInvariant() switch
    {
        "card" or "apple_pay" => PaymentMethod.Card,
        "mobile_money" => PaymentMethod.MobileMoney,
        "bank" or "bank_transfer" or "dedicated_nuban" or "eft" => PaymentMethod.BankTransfer,
        _ => PaymentMethod.Other
    };

    private static void EnsurePayable(InvoiceDto invoice)
    {
        if (!string.Equals(invoice.Currency?.Trim(), SupportedCurrency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Only {SupportedCurrency} invoices can be paid online.");
        if (invoice.Status is not (InvoiceStatus.Sent or InvoiceStatus.PartiallyPaid or InvoiceStatus.Overdue))
            throw new InvalidOperationException($"An invoice in status {invoice.Status} cannot be paid online.");
        if (invoice.AmountDue <= 0)
            throw new InvalidOperationException("This invoice has no balance due.");
    }

    private async Task LockCheckoutCreationAsync(string invoiceId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational())
            return;
        // Two-int form: its own key space, so it never collides with billing's single-key document locks.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({CheckoutLockClass}, hashtext({invoiceId}))",
            cancellationToken);
    }

    private static CheckoutSessionDto ToSession(PaymentCheckout c, bool reused) => new()
    {
        Reference = c.Reference,
        AuthorizationUrl = c.AuthorizationUrl!,
        Amount = c.Amount,
        Currency = c.Currency,
        Reused = reused
    };

    private static CheckoutStatusDto ToStatus(PaymentCheckout c) => new()
    {
        Reference = c.Reference,
        Status = c.Status,
        Amount = c.Amount,
        Currency = c.Currency,
        ReceiptId = c.ReceiptId,
        Message = c.LastError,
        CreatedAt = c.CreatedAt,
        PaidAt = c.PaidAt
    };

    private static string Truncate(string value) => Truncate(value, 500)!;

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
