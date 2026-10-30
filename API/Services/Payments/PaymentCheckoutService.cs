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
    private const int ReconcileBatchSize = 25;

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
            dto.CollectedTotal = await db.PaymentCheckouts
                .IgnoreQueryFilters()
                .Where(c => c.Status == PaymentCheckoutStatus.Paid || c.Status == PaymentCheckoutStatus.NeedsReview)
                .SumAsync(c => (decimal?)c.Amount, cancellationToken) ?? 0m;
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

        var invoice = await billing.GetInvoiceAsync(invoiceId, cancellationToken)
            ?? throw new NotFoundException("Invoice not found.");
        EnsurePayable(invoice);

        var customer = await db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == invoice.CustomerId, cancellationToken)
            ?? throw new NotFoundException("Customer not found.");
        var email = CustomerContactResolver.Billing(customer);

        return await RetriableTransaction.ExecuteAsync(db, async (transaction, ct) =>
        {
            await LockInvoiceRowAsync(invoiceId, ct);

            // Re-read the balance under the lock so a payment recorded meanwhile is respected.
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
                && now - c.CreatedAt < CheckoutLifetime);
            if (reusable is not null)
            {
                if (transaction is not null)
                    await transaction.CommitAsync(ct);
                return ToSession(reusable, reused: true);
            }

            // Checkouts for a different (stale) amount are superseded. If one is still paid later,
            // the webhook re-verifies it and it is applied or flagged NeedsReview — never lost.
            foreach (var stale in open)
            {
                stale.Status = PaymentCheckoutStatus.Expired;
                stale.CompletedAt = now;
                stale.LastError = "Superseded by a newer checkout.";
            }

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
                CreatedAt = now
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
        string? eventName;
        string? reference;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            eventName = root.TryGetProperty("event", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            reference = root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("reference", out var r)
                && r.ValueKind == JsonValueKind.String
                    ? r.GetString()
                    : null;
        }
        catch (JsonException)
        {
            logger.LogWarning("Paystack webhook body is not valid JSON; ignored.");
            return;
        }

        if (!string.Equals(eventName, "charge.success", StringComparison.Ordinal))
        {
            logger.LogInformation("Paystack webhook event {Event} ignored.", eventName ?? "(none)");
            return;
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            logger.LogWarning("Paystack charge.success webhook without a reference; ignored.");
            return;
        }

        var checkout = await db.PaymentCheckouts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Reference == reference, cancellationToken);
        if (checkout is null)
        {
            // Not ours (e.g. a payment link or another integration on the same Paystack account).
            logger.LogInformation("Paystack webhook for unknown reference {Reference}; ignored.", reference);
            return;
        }

        // The payload is only a hint: ConfirmAsync re-verifies status, amount and currency with Paystack.
        await ConfirmAsync(checkout, cancellationToken);
    }

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

        foreach (var item in due)
        {
            try
            {
                var checkout = await db.PaymentCheckouts.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(c => c.Id == item.Id, cancellationToken);
                if (checkout is not null)
                    await ConfirmAsync(checkout, cancellationToken);
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

        return due.Count;
    }

    /// <summary>
    /// Verifies the checkout with Paystack and applies the outcome. Gateway errors are recorded on the
    /// checkout (it stays Pending for the reconcile worker) instead of being thrown.
    /// </summary>
    internal async Task<PaymentCheckout> ConfirmAsync(PaymentCheckout checkout, CancellationToken cancellationToken)
    {
        if (checkout.Status is PaymentCheckoutStatus.Paid or PaymentCheckoutStatus.NeedsReview)
            return checkout;

        var now = DateTime.UtcNow;
        GatewayVerification verification;
        try
        {
            verification = await gateway.VerifyAsync(checkout.Reference, cancellationToken);
        }
        catch (PaymentGatewayException ex)
        {
            checkout.LastCheckedAt = now;
            checkout.LastError = Truncate(ex.Message);
            await db.SaveChangesAsync(cancellationToken);
            return checkout;
        }

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

        await db.SaveChangesAsync(cancellationToken);
        return checkout;
    }

    private async Task<PaymentCheckout> ApplySuccessAsync(
        PaymentCheckout checkout,
        GatewayVerification verification,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        checkout.Channel = Truncate(verification.Channel, 50);
        checkout.ProviderTransactionId = Truncate(verification.ProviderTransactionId, 100);
        checkout.PaidAt = verification.PaidAt ?? now;

        if (!string.Equals(verification.Currency, checkout.Currency, StringComparison.OrdinalIgnoreCase)
            || verification.AmountMinor != checkout.AmountMinor)
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
        await db.SaveChangesAsync(cancellationToken);
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
        await db.SaveChangesAsync(cancellationToken);
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
        await db.SaveChangesAsync(cancellationToken);
        return checkout;
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

    private async Task LockInvoiceRowAsync(string invoiceId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational())
            return;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "Invoices" WHERE "Id" = {invoiceId} FOR UPDATE""",
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
