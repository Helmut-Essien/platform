using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Data;
using Platform.Api.Services.Payments;
using Platform.Shared.Dtos.Payments;
using Platform.Shared.Enums;

namespace Platform.Api.Services.Renewals;

/// <summary>
/// Anonymous pay-link flow. Every failure to resolve a token (bad shape, unknown invoice, wrong signature,
/// revoked) is the same 404 with the same work done, so tokens and invoice ids can't be enumerated.
/// A correctly signed but expired link is 410 so the payer knows to ask for a new one.
/// </summary>
public sealed class PublicPayService(
    AppDbContext db,
    PayLinkService payLinks,
    IBillingService billing,
    IPaymentCheckoutService checkout,
    IOptions<PaystackOptions> paystack,
    IOptions<AuthSettings> auth)
{
    private const string InvalidLink = "This payment link is not valid.";

    public async Task<PublicInvoiceSummaryDto> GetSummaryAsync(string token, CancellationToken cancellationToken)
    {
        var invoiceId = await ResolveAsync(token, cancellationToken);
        var invoice = await billing.GetInvoiceAsync(invoiceId, cancellationToken) ?? throw new NotFoundException(InvalidLink);
        var issuer = await db.InvoiceBrandProfiles.AsNoTracking().Select(b => b.CompanyName).FirstOrDefaultAsync(cancellationToken);
        var payable = PayLinkService.IsPayableStatus(invoice.Status) && invoice.AmountDue > 0;
        return new PublicInvoiceSummaryDto
        {
            InvoiceNumber = invoice.InvoiceNumber,
            CustomerName = invoice.CustomerName ?? string.Empty,
            IssuerName = issuer,
            Currency = invoice.Currency,
            TotalAmount = invoice.TotalAmount,
            AmountPaid = invoice.AmountPaid,
            AmountDue = invoice.AmountDue,
            DueDate = invoice.DueDate ?? invoice.IssueDate,
            State = invoice.Status == InvoiceStatus.Paid ? "paid" : payable ? "payable" : "closed",
            OnlinePaymentAvailable = payable
                && paystack.Value.Enabled
                && string.Equals(invoice.Currency, PaymentCheckoutService.SupportedCurrency, StringComparison.OrdinalIgnoreCase),
            TestMode = paystack.Value.IsTestMode
        };
    }

    public async Task<CheckoutSessionDto> StartCheckoutAsync(string token, CancellationToken cancellationToken)
    {
        var invoiceId = await ResolveAsync(token, cancellationToken);
        var page = $"{auth.Value.ClientBaseUrl.TrimEnd('/')}/pay/{Uri.EscapeDataString(token)}";
        return await checkout.StartCheckoutAsync(
            invoiceId, PaymentCheckoutService.PublicActor, $"{page}?payment=return", $"{page}?payment=cancelled", cancellationToken);
    }

    public async Task<PublicCheckoutStatusDto> GetCheckoutStatusAsync(string token, string? reference, CancellationToken cancellationToken)
    {
        var invoiceId = await ResolveAsync(token, cancellationToken);
        var status = await checkout.GetCheckoutStatusAsync(invoiceId, reference, cancellationToken);
        return new PublicCheckoutStatusDto
        {
            Status = status.Status,
            Amount = status.Amount,
            Currency = status.Currency,
            Message = status.Message,
            PaidAt = status.PaidAt
        };
    }

    internal async Task<string> ResolveAsync(string token, CancellationToken cancellationToken)
    {
        if (!payLinks.Configured)
            throw new NotFoundException(InvalidLink);
        var shaped = PayLinkService.TryGetInvoiceId(token, out var invoiceId);
        // Always one indexed PK lookup, even for junk, so response time doesn't reveal what was rejected where.
        var row = await db.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.Id == (shaped ? invoiceId : "-"))
            .Select(i => new { i.PayLinkNonce, i.PayLinkExpiresAt })
            .FirstOrDefaultAsync(cancellationToken);
        var valid = payLinks.Verify(shaped ? token : new string('0', 27), shaped ? invoiceId : "-", row?.PayLinkNonce);
        if (!valid || row?.PayLinkExpiresAt is not { } expiresAt)
            throw new NotFoundException(InvalidLink);
        if (expiresAt <= DateTime.UtcNow)
            throw new GoneException("This payment link has expired. Please ask for a new invoice link.");
        return invoiceId;
    }
}
