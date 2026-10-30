using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Controllers;
using Platform.Api.Data;
using Platform.Api.Services;
using Platform.Api.Services.Email;
using Platform.Api.Services.Payments;
using Platform.Api.Services.Renewals;
using Platform.Shared.Enums;
using Xunit;
using static API.Tests.RenewalTests;

namespace API.Tests;

public class PayLinkTests
{
    private static string TokenOf(string url) => url[(url.LastIndexOf('/') + 1)..];

    private static async Task<(AppDbContext Db, BillingService Billing, PayLinkService Links, string InvoiceId)> SetupAsync(
        bool paystackEnabled = true)
    {
        var db = CreateDb();
        var links = CreatePayLinks(paystackEnabled);
        var billing = CreateBilling(db, payLinks: links);
        var expiry = DateTime.UtcNow.Date.AddDays(10);
        var license = await SeedLicenseAsync(db, expiry);
        var invoice = await billing.CreateRenewalInvoiceAsync(license.Id, expiry, BillingService.RenewalActor);
        return (db, billing, links, invoice!.Id);
    }

    private static PublicPayService CreatePublic(AppDbContext db, BillingService billing, PayLinkService links,
        RecordingGateway? gateway = null, bool paystackEnabled = true)
    {
        var paystack = Options.Create(new PaystackOptions { SecretKey = paystackEnabled ? "sk_test_0123456789abcdef" : null });
        var auth = Options.Create(new AuthSettings { ClientBaseUrl = "https://helmutcode.runasp.net/" });
        var checkout = new PaymentCheckoutService(db, billing, gateway ?? new RecordingGateway(), paystack, auth,
            NullLogger<PaymentCheckoutService>.Instance);
        return new PublicPayService(db, links, billing, checkout, paystack, auth);
    }

    [Fact]
    public async Task InvoiceEmail_ContainsPayLink_ThatResolves()
    {
        var (db, billing, links, invoiceId) = await SetupAsync();
        await using var _ = db;
        var email = await db.EmailOutboxMessages.SingleAsync(m => m.Kind == EmailDeliveryKind.Invoice);
        var url = await billing.GetPayLinkAsync(invoiceId);
        Assert.NotNull(url);
        Assert.StartsWith("https://helmutcode.runasp.net/pay/", url);
        Assert.Contains(url!, email.HtmlBody);
        Assert.True(TokenOf(url).Length <= PayLinkService.MaxTokenLength);

        var summary = await CreatePublic(db, billing, links).GetSummaryAsync(TokenOf(url), default);
        Assert.Equal("payable", summary.State);
        Assert.True(summary.OnlinePaymentAvailable);
        Assert.Equal(1200m, summary.AmountDue);
        Assert.Equal("Acme Ltd", summary.CustomerName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAV.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task UnknownOrMalformedToken_IsUniform404(string token)
    {
        var (db, billing, links, _) = await SetupAsync();
        await using var __ = db;
        await Assert.ThrowsAsync<NotFoundException>(() => CreatePublic(db, billing, links).GetSummaryAsync(token, default));
    }

    [Fact]
    public async Task TamperedSignature_Or_OtherInvoiceId_Is404()
    {
        var (db, billing, links, invoiceId) = await SetupAsync();
        await using var _ = db;
        var token = TokenOf((await billing.GetPayLinkAsync(invoiceId))!);
        var pub = CreatePublic(db, billing, links);

        var flipped = token[..^2] + (token[^2] == 'A' ? 'B' : 'A') + token[^1];
        await Assert.ThrowsAsync<NotFoundException>(() => pub.GetSummaryAsync(flipped, default));

        var other = NUlid.Ulid.NewUlid().ToString() + token[26..];
        await Assert.ThrowsAsync<NotFoundException>(() => pub.GetSummaryAsync(other, default));
        // ids are case-insensitive, the signature is not bypassable by case
        Assert.Equal(invoiceId, await pub.ResolveAsync(token[..26].ToLowerInvariant() + token[26..], default));
    }

    [Fact]
    public async Task ExpiredLink_Is410_AndVoidRevokes()
    {
        var (db, billing, links, invoiceId) = await SetupAsync();
        await using var _ = db;
        var token = TokenOf((await billing.GetPayLinkAsync(invoiceId))!);
        var pub = CreatePublic(db, billing, links);

        var invoice = await db.Invoices.SingleAsync(i => i.Id == invoiceId);
        invoice.PayLinkExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<GoneException>(() => pub.GetSummaryAsync(token, default));

        // re-issuing extends the same link (already-emailed links keep working)
        Assert.Equal(token, TokenOf((await billing.GetPayLinkAsync(invoiceId))!));
        await pub.GetSummaryAsync(token, default);

        await billing.VoidInvoiceAsync(invoiceId, "admin");
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<NotFoundException>(() => pub.GetSummaryAsync(token, default));
        Assert.Null(await billing.GetPayLinkAsync(invoiceId));
    }

    [Fact]
    public async Task PaidInvoice_LinkShowsPaid_ForOneDayOnly()
    {
        var (db, billing, links, invoiceId) = await SetupAsync();
        await using var _ = db;
        var token = TokenOf((await billing.GetPayLinkAsync(invoiceId))!);
        await PayInFullAsync(billing, (await billing.GetInvoiceAsync(invoiceId))!);
        db.ChangeTracker.Clear();

        var summary = await CreatePublic(db, billing, links).GetSummaryAsync(token, default);
        Assert.Equal("paid", summary.State);
        Assert.False(summary.OnlinePaymentAvailable);
        var expires = (await db.Invoices.SingleAsync(i => i.Id == invoiceId)).PayLinkExpiresAt!.Value;
        Assert.True(expires <= DateTime.UtcNow.AddDays(1).AddMinutes(1));
    }

    [Fact]
    public async Task PaystackDisabled_NoLinkIssued_AndCheckoutUnavailable()
    {
        var (db, billing, _, invoiceId) = await SetupAsync(paystackEnabled: false);
        await using var __ = db;
        Assert.Null(await billing.GetPayLinkAsync(invoiceId));
        var email = await db.EmailOutboxMessages.SingleAsync(m => m.Kind == EmailDeliveryKind.Invoice);
        Assert.DoesNotContain("/pay/", email.HtmlBody);

        // link issued while enabled, then Paystack switched off: page loads but says unavailable
        var enabled = CreatePayLinks();
        var url = enabled.EnsureUrl(await db.Invoices.SingleAsync(i => i.Id == invoiceId), DateTime.UtcNow)!;
        await db.SaveChangesAsync();
        var pub = CreatePublic(db, billing, CreatePayLinks(paystackEnabled: false), paystackEnabled: false);
        var summary = await pub.GetSummaryAsync(TokenOf(url), default);
        Assert.False(summary.OnlinePaymentAvailable);
        await Assert.ThrowsAsync<OnlinePaymentsDisabledException>(() => pub.StartCheckoutAsync(TokenOf(url), default));
    }

    [Fact]
    public async Task PublicCheckout_ReturnsToPayPage_AndIsNotReusedByAdmin()
    {
        var (db, billing, links, invoiceId) = await SetupAsync();
        await using var _ = db;
        var token = TokenOf((await billing.GetPayLinkAsync(invoiceId))!);
        var gateway = new RecordingGateway();
        var pub = CreatePublic(db, billing, links, gateway);

        var session = await pub.StartCheckoutAsync(token, default);
        var again = await pub.StartCheckoutAsync(token, default);
        Assert.Equal(session.Reference, again.Reference);
        Assert.Equal($"https://helmutcode.runasp.net/pay/{token}?payment=return", gateway.Requests[0].CallbackUrl);

        var checkout = new PaymentCheckoutService(db, billing, gateway,
            Options.Create(new PaystackOptions { SecretKey = "sk_test_0123456789abcdef" }),
            Options.Create(new AuthSettings { ClientBaseUrl = "https://helmutcode.runasp.net/" }),
            NullLogger<PaymentCheckoutService>.Instance);
        var admin = await checkout.StartCheckoutAsync(invoiceId, "admin@test");
        Assert.NotEqual(session.Reference, admin.Reference);
        Assert.Contains("/invoices/", gateway.Requests[1].CallbackUrl);

        var status = await pub.GetCheckoutStatusAsync(token, session.Reference, default);
        Assert.Equal(PaymentCheckoutStatus.Pending, status.Status);
    }

    [Fact]
    public void Signing_IsDeterministic_AndKeyed()
    {
        var a = CreatePayLinks();
        var b = new PayLinkService(
            Options.Create(new BillingSettings()),
            Options.Create(new JwtSettings { Key = "a-completely-different-jwt-key-0123456789abcdef" }),
            Options.Create(new AuthSettings()),
            Options.Create(new PaystackOptions { SecretKey = "sk_test_x" }));
        var id = NUlid.Ulid.NewUlid().ToString();
        var token = a.CreateToken(id, "nonce");
        Assert.Equal(token, a.CreateToken(id, "nonce"));
        Assert.True(a.Verify(token, id, "nonce"));
        Assert.False(a.Verify(token, id, "other"));
        Assert.False(a.Verify(token, id, null));
        Assert.False(b.Verify(token, id, "nonce"));
    }

    [Fact]
    public void PublicController_IsAnonymous_AndRateLimited()
    {
        var type = typeof(PublicPayController);
        Assert.NotNull(type.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal(Platform.Api.Extensions.RateLimitingExtensions.PublicPayPolicy,
            type.GetCustomAttribute<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()!.PolicyName);
        Assert.Null(type.GetCustomAttribute<AuthorizeAttribute>());
    }

    internal sealed class RecordingGateway : IPaymentGateway
    {
        public List<GatewayCheckoutRequest> Requests { get; } = [];

        public Task<GatewayCheckout> InitializeAsync(GatewayCheckoutRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new GatewayCheckout($"https://checkout.paystack.com/{request.Reference}", "code"));
        }

        public Task<GatewayVerification> VerifyAsync(string reference, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GatewayVerification(GatewayTransactionState.Pending, 0, null, null, null, null, "ongoing"));
    }
}
