using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Controllers;
using Platform.Api.Data;
using Platform.Api.Entities;
using Platform.Api.Services;
using Platform.Api.Services.Email;
using Platform.Api.Services.Payments;
using Platform.Shared.Dtos.Audit;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Enums;
using Xunit;

namespace API.Tests;

public class PaystackPaymentTests
{
    private const string Secret = "sk_test_0123456789abcdef";

    // ---- signature ----

    [Fact]
    public void Signature_ValidHmacSha512_IsAccepted()
    {
        var body = Encoding.UTF8.GetBytes("""{"event":"charge.success","data":{"reference":"PLT-1"}}""");
        var signature = PaystackSignature.Compute(Secret, body);

        Assert.True(PaystackSignature.IsValid(Secret, body, signature));
        Assert.True(PaystackSignature.IsValid(Secret, body, signature.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("abcd")]
    public void Signature_MissingOrMalformed_IsRejected(string? header)
    {
        var body = Encoding.UTF8.GetBytes("{}");
        Assert.False(PaystackSignature.IsValid(Secret, body, header));
    }

    [Fact]
    public void Signature_TamperedBodyOrWrongKey_IsRejected()
    {
        var body = Encoding.UTF8.GetBytes("""{"amount":100}""");
        var signature = PaystackSignature.Compute(Secret, body);

        Assert.False(PaystackSignature.IsValid(Secret, Encoding.UTF8.GetBytes("""{"amount":999}"""), signature));
        Assert.False(PaystackSignature.IsValid("sk_test_other", body, signature));
        Assert.False(PaystackSignature.IsValid(null, body, signature));
    }

    // ---- pesewas ----

    [Theory]
    [InlineData("0.01", 1L)]
    [InlineData("0.1", 10L)]
    [InlineData("12.34", 1234L)]
    [InlineData("30000", 3000000L)]
    [InlineData("1234567.89", 123456789L)]
    public void MinorUnits_ConvertsExactly(string amount, long expected)
    {
        var value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, MinorUnits.FromAmount(value));
        Assert.Equal(value, MinorUnits.ToAmount(expected));
    }

    [Fact]
    public void MinorUnits_RejectsSubPesewaAndNonPositive()
    {
        Assert.Throws<ArgumentException>(() => MinorUnits.FromAmount(1.005m));
        Assert.Throws<ArgumentOutOfRangeException>(() => MinorUnits.FromAmount(0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => MinorUnits.FromAmount(-5m));
    }

    [Theory]
    [InlineData("mobile_money", PaymentMethod.MobileMoney)]
    [InlineData("card", PaymentMethod.Card)]
    [InlineData("CARD", PaymentMethod.Card)]
    [InlineData("bank_transfer", PaymentMethod.BankTransfer)]
    [InlineData("ussd", PaymentMethod.Other)]
    [InlineData(null, PaymentMethod.Other)]
    public void MapChannel_UsesClosestMethod(string? channel, PaymentMethod expected) =>
        Assert.Equal(expected, PaymentCheckoutService.MapChannel(channel));

    // ---- disabled config ----

    [Fact]
    public async Task Disabled_StatusReportsOff_AndEndpointsThrow503Exception()
    {
        await using var db = CreateDbContext();
        var (service, _) = CreateService(db, secretKey: null);

        var status = await service.GetStatusAsync();
        Assert.False(status.Enabled);
        await Assert.ThrowsAsync<OnlinePaymentsDisabledException>(() => service.StartCheckoutAsync("x", "admin"));
        await Assert.ThrowsAsync<OnlinePaymentsDisabledException>(() => service.GetCheckoutStatusAsync("x", null));
        Assert.Equal(0, await service.ReconcileAsync());
    }

    [Fact]
    public void Options_EnabledOnlyWithKeyAndHttpsBaseUrl()
    {
        Assert.False(new PaystackOptions().Enabled);
        Assert.False(new PaystackOptions { SecretKey = "  " }.Enabled);
        Assert.True(new PaystackOptions { SecretKey = Secret }.Enabled);
        Assert.True(new PaystackOptions { SecretKey = Secret }.IsTestMode);
        Assert.False(new PaystackOptions { SecretKey = Secret, BaseUrl = "http://api.paystack.co" }.Enabled);
        Assert.False(new PaystackOptions { SecretKey = Secret, BaseUrl = "not a url" }.Enabled);
    }

    [Fact]
    public async Task Webhook_Disabled_Returns503()
    {
        await using var db = CreateDbContext();
        var (service, _) = CreateService(db, secretKey: null);
        var controller = CreateController(service, secretKey: null, body: "{}", signature: null);

        var result = await controller.PaystackWebhook(CancellationToken.None);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    // ---- checkout creation ----

    [Fact]
    public async Task StartCheckout_ChargesOutstandingBalance_AndReusesOpenCheckout()
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db);
        var billing = CreateBilling(db);
        var invoice = await CreateInvoiceAsync(db, billing, 100m);
        await billing.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "cash-1",
            AmountPaid = 40m,
            PaymentMethod = PaymentMethod.Cash
        }, "admin");

        var first = await service.StartCheckoutAsync(invoice.Id, "admin");
        Assert.Equal(60m, first.Amount);
        Assert.False(first.Reused);
        var request = Assert.Single(gateway.Initialized);
        Assert.Equal(6000L, request.AmountMinor);
        Assert.Equal("GHS", request.Currency);
        Assert.Equal("billing@acme.test", request.Email);
        Assert.Equal($"https://helmutcode.runasp.net/invoices/{invoice.Id}?payment=return", request.CallbackUrl);
        Assert.StartsWith("PLT-", request.Reference);

        var second = await service.StartCheckoutAsync(invoice.Id, "admin");
        Assert.True(second.Reused);
        Assert.Equal(first.Reference, second.Reference);
        Assert.Single(gateway.Initialized);
    }

    [Fact]
    public async Task StartCheckout_NewAmount_KeepsOldCheckoutPendingForReconcile()
    {
        await using var db = CreateDbContext();
        var (service, _) = CreateService(db);
        var billing = CreateBilling(db);
        var invoice = await CreateInvoiceAsync(db, billing, 100m);

        var first = await service.StartCheckoutAsync(invoice.Id, "admin");
        await billing.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "cash-1", AmountPaid = 25m, PaymentMethod = PaymentMethod.Cash
        }, "admin");
        var second = await service.StartCheckoutAsync(invoice.Id, "admin");

        Assert.NotEqual(first.Reference, second.Reference);
        Assert.Equal(75m, second.Amount);
        var old = await db.PaymentCheckouts.IgnoreQueryFilters().SingleAsync(c => c.Reference == first.Reference);
        // Not expired blindly: the payer may still be on that page; reconcile verifies and expires it later.
        Assert.Equal(PaymentCheckoutStatus.Pending, old.Status);
    }

    [Fact]
    public async Task StartCheckout_OldOpenCheckout_IsNotReused()
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db);
        var billing = CreateBilling(db);
        var invoice = await CreateInvoiceAsync(db, billing, 10m);
        var first = await service.StartCheckoutAsync(invoice.Id, "admin");
        var row = await db.PaymentCheckouts.IgnoreQueryFilters().SingleAsync();
        row.CreatedAt = DateTime.UtcNow - PaymentCheckoutService.CheckoutReuseWindow - TimeSpan.FromMinutes(1);
        await db.SaveChangesAsync();

        var second = await service.StartCheckoutAsync(invoice.Id, "admin");

        Assert.False(second.Reused);
        Assert.NotEqual(first.Reference, second.Reference);
        Assert.Equal(2, gateway.Initialized.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    public async Task StartCheckout_InvalidCustomerEmail_IsRejectedBeforeCallingPaystack(string email)
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db);
        var billing = CreateBilling(db);
        var invoice = await CreateInvoiceAsync(db, billing, 10m);
        var customer = await db.Customers.SingleAsync();
        customer.ContactEmail = email;
        customer.BillingEmail = null;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartCheckoutAsync(invoice.Id, "admin"));
        Assert.Contains("email", ex.Message);
        Assert.Empty(gateway.Initialized);
    }

    [Fact]
    public async Task StartCheckout_RecordsLiveMode()
    {
        await using var db = CreateDbContext();
        var (service, _) = CreateService(db, secretKey: "sk_live_abc");
        var billing = CreateBilling(db);
        var invoice = await CreateInvoiceAsync(db, billing, 10m);

        await service.StartCheckoutAsync(invoice.Id, "admin");

        Assert.True((await db.PaymentCheckouts.IgnoreQueryFilters().SingleAsync()).LiveMode);
    }

    [Fact]
    public async Task StartCheckout_RejectsVoidedPaidAndNonGhsInvoices()
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db);
        var billing = CreateBilling(db);

        var voided = await CreateInvoiceAsync(db, billing, 50m);
        await billing.VoidInvoiceAsync(voided.Id, "admin");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartCheckoutAsync(voided.Id, "admin"));

        var paid = await CreateInvoiceAsync(db, billing, 50m);
        await billing.RecordReceiptAsync(paid.Id, new RecordReceiptRequest
        {
            IdempotencyKey = "cash-2", AmountPaid = 50m, PaymentMethod = PaymentMethod.Cash
        }, "admin");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartCheckoutAsync(paid.Id, "admin"));

        var usd = await CreateInvoiceAsync(db, billing, 50m, currency: "USD");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartCheckoutAsync(usd.Id, "admin"));

        await Assert.ThrowsAsync<NotFoundException>(() => service.StartCheckoutAsync("missing", "admin"));
        Assert.Empty(gateway.Initialized);
    }

    // ---- webhook / verify ----

    [Fact]
    public async Task Webhook_DuplicateChargeSuccess_RecordsOneReceipt()
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db);
        var billing = CreateBilling(db);
        var invoice = await CreateInvoiceAsync(db, billing, 120.50m);
        var checkout = await service.StartCheckoutAsync(invoice.Id, "admin");
        var paidAt = DateTime.UtcNow.AddMinutes(-1);
        gateway.Verification = new GatewayVerification(
            GatewayTransactionState.Success, 12050, "GHS", "mobile_money", paidAt, "987654", "Approved");

        var body = WebhookBody(checkout.Reference);
        await service.HandleWebhookAsync(body);
        db.ChangeTracker.Clear();
        await service.HandleWebhookAsync(body);
        db.ChangeTracker.Clear();

        var receipt = await db.Receipts.IgnoreQueryFilters().SingleAsync(r => r.InvoiceId == invoice.Id);
        Assert.Equal(120.50m, receipt.AmountPaid);
        Assert.Equal(PaymentMethod.MobileMoney, receipt.PaymentMethod);
        Assert.Equal(checkout.Reference, receipt.PaymentReference);
        Assert.Equal(paidAt, receipt.PaidAt, TimeSpan.FromSeconds(1));
        var tx = await db.PaymentTransactions.IgnoreQueryFilters().SingleAsync(t => t.InvoiceId == invoice.Id);
        Assert.Equal($"paystack:{checkout.Reference}", tx.IdempotencyKey);

        var stored = await db.PaymentCheckouts.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PaymentCheckoutStatus.Paid, stored.Status);
        Assert.Equal(receipt.Id, stored.ReceiptId);
        var updated = await db.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == invoice.Id);
        Assert.Equal(InvoiceStatus.Paid, updated.Status);

        var status = await service.GetStatusAsync();
        Assert.Equal(120.50m, status.CollectedTotal);
    }

    [Fact]
    public async Task Webhook_AmountMismatch_IsNotRecorded_AndFlaggedForReview()
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db);
        var billing = CreateBilling(db);
        var invoice = await CreateInvoiceAsync(db, billing, 100m);
        var checkout = await service.StartCheckoutAsync(invoice.Id, "admin");
        gateway.Verification = new GatewayVerification(
            GatewayTransactionState.Success, 100, "GHS", "card", DateTime.UtcNow, "1", "Approved");

        await service.HandleWebhookAsync(WebhookBody(checkout.Reference));

        Assert.Empty(await db.Receipts.IgnoreQueryFilters().ToListAsync());
        var stored = await db.PaymentCheckouts.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PaymentCheckoutStatus.NeedsReview, stored.Status);
    }

    [Fact]
    public async Task Webhook_Overpayment_IsFlagged()
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db);
        var billing = CreateBilling(db);
        var invoice = await CreateInvoiceAsync(db, billing, 100m);
        var checkout = await service.StartCheckoutAsync(invoice.Id, "admin");
        gateway.Verification = new GatewayVerification(
            GatewayTransactionState.Success, 10001, "GHS", "card", DateTime.UtcNow, "1", null);

        await service.HandleWebhookAsync(WebhookBody(checkout.Reference));

        Assert.Equal(PaymentCheckoutStatus.NeedsReview, (await db.PaymentCheckouts.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Fact]
    public async Task Webhook_FeesPassedToCustomer_RecordsInvoiceAmount_AndCapCountsCharged()
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db);
        var billing = CreateBilling(db);
        var invoice = await CreateInvoiceAsync(db, billing, 100m);
        var checkout = await service.StartCheckoutAsync(invoice.Id, "admin");
        gateway.Verification = new GatewayVerification(
            GatewayTransactionState.Success, 10195, "ghs", "card", DateTime.UtcNow.AddHours(3), "1", null,
            RequestedAmountMinor: 10000);

        await service.HandleWebhookAsync(WebhookBody(checkout.Reference));

        var receipt = await db.Receipts.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(100m, receipt.AmountPaid);
        Assert.True(receipt.PaidAt <= DateTime.UtcNow.AddSeconds(1)); // future paid_at clamped
        Assert.Equal(101.95m, (await service.GetStatusAsync()).CollectedTotal);
    }

    [Fact]
    public async Task Cap_CountsOnlyCurrentMode()
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db);
        var billing = CreateBilling(db);
        var invoice = await CreateInvoiceAsync(db, billing, 50m);
        var checkout = await service.StartCheckoutAsync(invoice.Id, "admin");
        gateway.Verification = new GatewayVerification(
            GatewayTransactionState.Success, 5000, "GHS", "card", DateTime.UtcNow, "1", null);
        await service.HandleWebhookAsync(WebhookBody(checkout.Reference));
        db.ChangeTracker.Clear();

        var (liveService, _) = CreateService(db, secretKey: "sk_live_abc");
        Assert.Equal(50m, (await service.GetStatusAsync()).CollectedTotal);
        Assert.Equal(0m, (await liveService.GetStatusAsync()).CollectedTotal);
    }

    [Fact]
    public async Task ConcurrentConfirm_StaleCopyCannotOverwritePaid()
    {
        var name = NUlid.Ulid.NewUlid().ToString();
        await using var db1 = CreateDbContext(name);
        await using var db2 = CreateDbContext(name);
        var (service1, gateway1) = CreateService(db1);
        var billing = CreateBilling(db1);
        var invoice = await CreateInvoiceAsync(db1, billing, 10m);
        var checkout = await service1.StartCheckoutAsync(invoice.Id, "admin");

        // The worker loaded the row while it was still Pending...
        var (service2, gateway2) = CreateService(db2);
        var stale = await db2.PaymentCheckouts.IgnoreQueryFilters().SingleAsync();

        // ...then the webhook applied the payment.
        gateway1.Verification = new GatewayVerification(GatewayTransactionState.Success, 1000, "GHS", "card", DateTime.UtcNow, "1", null);
        await service1.HandleWebhookAsync(WebhookBody(checkout.Reference));

        gateway2.Verification = new GatewayVerification(GatewayTransactionState.Failed, 1000, "GHS", "card", null, "1", "reversed");
        var result = await service2.ConfirmAsync(stale, CancellationToken.None);

        Assert.Equal(PaymentCheckoutStatus.Paid, result.Status);
        await using var db3 = CreateDbContext(name);
        Assert.Equal(PaymentCheckoutStatus.Paid, (await db3.PaymentCheckouts.IgnoreQueryFilters().SingleAsync()).Status);
        Assert.Single(await db3.Receipts.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task ConcurrentSuccess_FromTwoCallers_RecordsOneReceipt()
    {
        var name = NUlid.Ulid.NewUlid().ToString();
        await using var db1 = CreateDbContext(name);
        await using var db2 = CreateDbContext(name);
        var (service1, gateway1) = CreateService(db1);
        var billing = CreateBilling(db1);
        var invoice = await CreateInvoiceAsync(db1, billing, 10m);
        var checkout = await service1.StartCheckoutAsync(invoice.Id, "admin");
        var (service2, gateway2) = CreateService(db2);
        var stale = await db2.PaymentCheckouts.IgnoreQueryFilters().SingleAsync();
        var ok = new GatewayVerification(GatewayTransactionState.Success, 1000, "GHS", "card", DateTime.UtcNow, "1", null);
        gateway1.Verification = gateway2.Verification = ok;

        await service1.HandleWebhookAsync(WebhookBody(checkout.Reference));
        var result = await service2.ConfirmAsync(stale, CancellationToken.None);

        Assert.Equal(PaymentCheckoutStatus.Paid, result.Status);
        await using var db3 = CreateDbContext(name);
        Assert.Single(await db3.Receipts.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Webhook_CurrencyMismatch_IsNotRecorded()
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db);
        var billing = CreateBilling(db);
        var invoice = await CreateInvoiceAsync(db, billing, 10m);
        var checkout = await service.StartCheckoutAsync(invoice.Id, "admin");
        gateway.Verification = new GatewayVerification(
            GatewayTransactionState.Success, 1000, "NGN", "card", DateTime.UtcNow, "1", null);

        await service.HandleWebhookAsync(WebhookBody(checkout.Reference));

        Assert.Empty(await db.Receipts.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Webhook_VerifyFailure_LeavesCheckoutPending()
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db);
        var billing = CreateBilling(db);
        var invoice = await CreateInvoiceAsync(db, billing, 10m);
        var checkout = await service.StartCheckoutAsync(invoice.Id, "admin");
        gateway.VerifyError = new PaymentGatewayException("Paystack could not be reached.");

        await service.HandleWebhookAsync(WebhookBody(checkout.Reference));

        var stored = await db.PaymentCheckouts.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PaymentCheckoutStatus.Pending, stored.Status);
        Assert.NotNull(stored.LastError);
        Assert.Empty(await db.Receipts.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Webhook_PaymentForVoidedInvoice_IsFlaggedNotLost()
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db);
        var billing = CreateBilling(db);
        var invoice = await CreateInvoiceAsync(db, billing, 10m);
        var checkout = await service.StartCheckoutAsync(invoice.Id, "admin");
        await billing.VoidInvoiceAsync(invoice.Id, "admin");
        gateway.Verification = new GatewayVerification(
            GatewayTransactionState.Success, 1000, "GHS", "card", DateTime.UtcNow, "1", null);

        await service.HandleWebhookAsync(WebhookBody(checkout.Reference));

        var stored = await db.PaymentCheckouts.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PaymentCheckoutStatus.NeedsReview, stored.Status);
        Assert.Empty(await db.Receipts.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Webhook_IgnoresOtherEventsAndUnknownReferences()
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db);

        await service.HandleWebhookAsync(Encoding.UTF8.GetBytes("""{"event":"transfer.success","data":{"reference":"x"}}"""));
        await service.HandleWebhookAsync(WebhookBody("unknown-ref"));
        await service.HandleWebhookAsync(Encoding.UTF8.GetBytes("not json"));

        Assert.Equal(0, gateway.VerifyCalls);
    }

    [Fact]
    public async Task WebhookEndpoint_OversizedBody_Returns413()
    {
        await using var db = CreateDbContext();
        var (service, _) = CreateService(db);
        var body = new string('x', 64 * 1024 + 1);
        var controller = CreateController(service, Secret, body, PaystackSignature.Compute(Secret, Encoding.UTF8.GetBytes(body)));

        var result = await controller.PaystackWebhook(CancellationToken.None);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    [Fact]
    public async Task WebhookEndpoint_InvalidSignature_Returns401_ValidReturns200()
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db);
        var body = """{"event":"charge.success","data":{"reference":"unknown"}}""";

        var bad = CreateController(service, Secret, body, signature: new string('a', 128));
        Assert.IsType<UnauthorizedResult>(await bad.PaystackWebhook(CancellationToken.None));

        var good = CreateController(service, Secret, body, PaystackSignature.Compute(Secret, Encoding.UTF8.GetBytes(body)));
        Assert.IsType<OkResult>(await good.PaystackWebhook(CancellationToken.None));
        Assert.Equal(0, gateway.VerifyCalls);
    }

    // ---- status / reconcile ----

    [Fact]
    public async Task CheckoutStatus_VerifiesOnDemand_AndMarksFailed()
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db);
        var billing = CreateBilling(db);
        var invoice = await CreateInvoiceAsync(db, billing, 10m);
        var checkout = await service.StartCheckoutAsync(invoice.Id, "admin");
        gateway.Verification = new GatewayVerification(GatewayTransactionState.Failed, 1000, "GHS", "card", null, "1", "Declined");

        var status = await service.GetCheckoutStatusAsync(invoice.Id, checkout.Reference);

        Assert.Equal(PaymentCheckoutStatus.Failed, status.Status);
        Assert.Equal("Declined", status.Message);
    }

    [Fact]
    public async Task Reconcile_AppliesMissedPayments_AndExpiresStaleCheckouts()
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db);
        var billing = CreateBilling(db);
        var paidInvoice = await CreateInvoiceAsync(db, billing, 10m);
        var staleInvoice = await CreateInvoiceAsync(db, billing, 20m);
        var paid = await service.StartCheckoutAsync(paidInvoice.Id, "admin");
        var stale = await service.StartCheckoutAsync(staleInvoice.Id, "admin");
        var fresh = await CreateInvoiceAsync(db, billing, 30m);
        await service.StartCheckoutAsync(fresh.Id, "admin"); // too new to reconcile

        foreach (var c in await db.PaymentCheckouts.IgnoreQueryFilters().ToListAsync())
        {
            if (c.Reference == paid.Reference) c.CreatedAt = DateTime.UtcNow.AddMinutes(-5);
            if (c.Reference == stale.Reference) c.CreatedAt = DateTime.UtcNow.AddHours(-25);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        gateway.VerifyByReference[paid.Reference] = new GatewayVerification(
            GatewayTransactionState.Success, 1000, "GHS", "card", DateTime.UtcNow, "1", null);
        gateway.VerifyByReference[stale.Reference] = new GatewayVerification(
            GatewayTransactionState.Pending, 0, null, null, null, null, "abandoned");

        var checkedCount = await service.ReconcileAsync();

        Assert.Equal(2, checkedCount);
        var all = await db.PaymentCheckouts.IgnoreQueryFilters().AsNoTracking().ToListAsync();
        Assert.Equal(PaymentCheckoutStatus.Paid, all.Single(c => c.Reference == paid.Reference).Status);
        Assert.Equal(PaymentCheckoutStatus.Expired, all.Single(c => c.Reference == stale.Reference).Status);
        Assert.Single(all, c => c.Status == PaymentCheckoutStatus.Pending);
    }

    // ---- gateway HTTP parsing ----

    [Fact]
    public async Task Gateway_ParsesVerifyAndSendsBearerKey()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"status":true,"message":"Verification successful","data":{"id":4099260516,"status":"success","reference":"PLT-1",
             "amount":12050,"currency":"GHS","channel":"mobile_money","paid_at":"2026-10-30T08:15:00.000Z","gateway_response":"Approved"}}
            """);
        var gateway = CreateGateway(handler);

        var result = await gateway.VerifyAsync("PLT-1");

        Assert.Equal(GatewayTransactionState.Success, result.State);
        Assert.Equal(12050, result.AmountMinor);
        Assert.Equal("GHS", result.Currency);
        Assert.Equal("mobile_money", result.Channel);
        Assert.Equal(new DateTime(2026, 10, 30, 8, 15, 0, DateTimeKind.Utc), result.PaidAt);
        Assert.Equal("https://api.paystack.co/transaction/verify/PLT-1", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task Gateway_UnknownReference_IsNotFound_AndHttpErrorsThrowWithoutSecret()
    {
        var notFound = CreateGateway(new StubHandler(HttpStatusCode.BadRequest,
            """{"status":false,"message":"Transaction reference not found"}"""));
        Assert.Equal(GatewayTransactionState.NotFound, (await notFound.VerifyAsync("x")).State);

        var unauthorized = CreateGateway(new StubHandler(HttpStatusCode.Unauthorized,
            """{"status":false,"message":"Invalid key"}"""));
        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() => unauthorized.VerifyAsync("x"));
        Assert.DoesNotContain(Secret, ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Gateway_RateLimitOrServerError_ThrowsGatewayException(HttpStatusCode status)
    {
        var gateway = CreateGateway(new StubHandler(status, "<html>busy</html>"));
        await Assert.ThrowsAsync<PaymentGatewayException>(() => gateway.VerifyAsync("PLT-1"));
    }

    [Fact]
    public async Task Gateway_Timeout_IsGatewayException_ButCallerCancellationPropagates()
    {
        var timeout = CreateGateway(new ThrowingHandler(new TaskCanceledException("timeout")));
        await Assert.ThrowsAsync<PaymentGatewayException>(() => timeout.VerifyAsync("PLT-1"));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = CreateGateway(new ThrowingHandler(new TaskCanceledException("cancelled")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.VerifyAsync("PLT-1", cts.Token));
    }

    [Fact]
    public async Task Gateway_ParsesRequestedAmount_AndOddPaidAt()
    {
        var gateway = CreateGateway(new StubHandler(HttpStatusCode.OK, """
            {"status":true,"message":"ok","data":{"id":1,"status":"success","amount":10195,"requested_amount":10000,
             "currency":"ghs","channel":"Card","paid_at":"not a date"}}
            """));

        var result = await gateway.VerifyAsync("PLT-1");

        Assert.Equal(10000, result.RequestedAmountMinor);
        Assert.Equal("GHS", result.Currency);
        Assert.Equal("card", result.Channel);
        Assert.Null(result.PaidAt);
    }

    [Fact]
    public async Task Gateway_InitializeSendsPesewasAsString()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"status":true,"message":"ok","data":{"authorization_url":"https://checkout.paystack.com/abc","access_code":"abc","reference":"PLT-1"}}""");
        var gateway = CreateGateway(handler);

        var result = await gateway.InitializeAsync(new GatewayCheckoutRequest(
            "PLT-1", 6000, "GHS", "a@b.test", "https://x/cb", new Dictionary<string, string>()));

        Assert.Equal("https://checkout.paystack.com/abc", result.AuthorizationUrl);
        Assert.Contains("\"amount\":\"6000\"", handler.LastBody);
        Assert.Contains("\"currency\":\"GHS\"", handler.LastBody);
    }

    // ---- round 2: refunds, disputes, modes, backoff, atomicity ----

    private static byte[] EventBody(string json) => Encoding.UTF8.GetBytes(json);

    private async Task<(AppDbContext Db, PaymentCheckoutService Service, FakeGateway Gateway, string InvoiceId, string Reference)> PaidCheckoutAsync()
    {
        var db = CreateDbContext();
        var (service, gateway) = CreateService(db);
        var billing = CreateBilling(db);
        var invoice = await CreateInvoiceAsync(db, billing, 10m);
        var checkout = await service.StartCheckoutAsync(invoice.Id, "admin");
        gateway.Verification = new GatewayVerification(GatewayTransactionState.Success, 1000, "GHS", "card", DateTime.UtcNow, "1", null);
        await service.HandleWebhookAsync(WebhookBody(checkout.Reference));
        db.ChangeTracker.Clear();
        return (db, service, gateway, invoice.Id, checkout.Reference);
    }

    [Fact]
    public async Task RefundWebhook_FlagsCheckout_KeepsReceipt_AndStillCountsTowardCap()
    {
        var (db, service, gateway, _, reference) = await PaidCheckoutAsync();
        await using var _db = db;

        await service.HandleWebhookAsync(EventBody($$$"""{"event":"refund.processed","data":{"transaction_reference":"{{{reference}}}","amount":1000,"currency":"GHS","domain":"test"}}"""));
        db.ChangeTracker.Clear();

        var row = await db.PaymentCheckouts.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PaymentCheckoutStatus.Refunded, row.Status);
        Assert.Contains("Refund 10.00 GHS", row.LastError);
        Assert.Single(await db.Receipts.IgnoreQueryFilters().ToListAsync()); // admin decides on reversal
        Assert.Equal(10m, (await service.GetStatusAsync()).CollectedTotal);

        // A replayed charge.success afterwards must not resurrect it.
        var calls = gateway.VerifyCalls;
        await service.HandleWebhookAsync(WebhookBody(reference));
        Assert.Equal(calls, gateway.VerifyCalls);
    }

    [Fact]
    public async Task DisputeWebhook_NestedReference_FlagsDisputed_ButNotOverRefund()
    {
        var (db, service, _, _, reference) = await PaidCheckoutAsync();
        await using var _db = db;
        var dispute = EventBody($$$"""{"event":"charge.dispute.create","data":{"transaction":{"reference":"{{{reference}}}"},"domain":"test"}}""");

        await service.HandleWebhookAsync(dispute);
        db.ChangeTracker.Clear();
        Assert.Equal(PaymentCheckoutStatus.Disputed, (await db.PaymentCheckouts.IgnoreQueryFilters().SingleAsync()).Status);

        await service.HandleWebhookAsync(EventBody($$$"""{"event":"refund.processed","data":{"transaction_reference":"{{{reference}}}"}}"""));
        db.ChangeTracker.Clear();
        await service.HandleWebhookAsync(dispute);
        db.ChangeTracker.Clear();
        Assert.Equal(PaymentCheckoutStatus.Refunded, (await db.PaymentCheckouts.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Fact]
    public async Task Webhook_FromOtherDomain_IsIgnored()
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db); // test key
        var billing = CreateBilling(db);
        var invoice = await CreateInvoiceAsync(db, billing, 10m);
        var checkout = await service.StartCheckoutAsync(invoice.Id, "admin");

        await service.HandleWebhookAsync(EventBody($$$"""{"event":"charge.success","data":{"reference":"{{{checkout.Reference}}}","domain":"live"}}"""));

        Assert.Equal(0, gateway.VerifyCalls);
    }

    [Fact]
    public async Task KeySwitch_TestCheckoutUnderLiveKey_ExpiresWithoutVerifyCall()
    {
        await using var db = CreateDbContext();
        var (testService, _) = CreateService(db);
        var billing = CreateBilling(db);
        var invoice = await CreateInvoiceAsync(db, billing, 10m);
        var checkout = await testService.StartCheckoutAsync(invoice.Id, "admin");

        var (liveService, liveGateway) = CreateService(db, secretKey: "sk_live_abc");
        var status = await liveService.GetCheckoutStatusAsync(invoice.Id, checkout.Reference);

        Assert.Equal(PaymentCheckoutStatus.Expired, status.Status);
        Assert.Contains("test mode", status.Message);
        Assert.Equal(0, liveGateway.VerifyCalls);
    }

    [Fact]
    public async Task Reconcile_BacksOffAfterRepeatedGatewayFailures()
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db);
        var billing = CreateBilling(db);
        for (var i = 0; i < 5; i++)
        {
            var invoice = await CreateInvoiceAsync(db, billing, 10m + i);
            await service.StartCheckoutAsync(invoice.Id, "admin");
        }
        foreach (var c in await db.PaymentCheckouts.IgnoreQueryFilters().ToListAsync())
            c.CreatedAt = DateTime.UtcNow.AddMinutes(-10);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        gateway.VerifyError = new PaymentGatewayException("Paystack verify failed (HTTP 429): rate limited");

        var processed = await service.ReconcileAsync();

        Assert.Equal(3, gateway.VerifyCalls);
        Assert.Equal(3, processed);
    }

    [Fact]
    public async Task CrashAfterReceiptCommit_RetryDoesNotDuplicateReceiptOrEmail()
    {
        await using var db = CreateDbContext();
        var (service, gateway) = CreateService(db);
        var billing = CreateBilling(db);
        var invoice = await CreateInvoiceAsync(db, billing, 10m);
        var checkout = await service.StartCheckoutAsync(invoice.Id, "admin");
        // Simulate: the receipt transaction committed, then the process died before the checkout was marked Paid.
        await billing.RecordReceiptAsync(invoice.Id, new RecordReceiptRequest
        {
            IdempotencyKey = $"paystack:{checkout.Reference}",
            AmountPaid = 10m,
            PaymentMethod = PaymentMethod.Card,
            PaymentReference = checkout.Reference
        }, PaymentCheckoutService.SystemActor);
        var emailsBefore = await db.EmailOutboxMessages.CountAsync();
        db.ChangeTracker.Clear();
        gateway.Verification = new GatewayVerification(GatewayTransactionState.Success, 1000, "GHS", "card", DateTime.UtcNow, "1", null);

        await service.ReconcileAsync(); // too new: not picked
        await service.HandleWebhookAsync(WebhookBody(checkout.Reference));
        db.ChangeTracker.Clear();

        Assert.Single(await db.Receipts.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(emailsBefore, await db.EmailOutboxMessages.CountAsync());
        Assert.Equal(PaymentCheckoutStatus.Paid, (await db.PaymentCheckouts.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Theory]
    [InlineData("""{"event":"charge.success","data":null}""", "charge.success", null)]
    [InlineData("""{"event":"charge.success","data":{"reference":123}}""", "charge.success", null)]
    [InlineData("""{"event":"refund.processed","data":{"transaction_reference":" R1 "}}""", "refund.processed", "R1")]
    [InlineData("""{"event":"charge.dispute.create","data":{"transaction":{"reference":"R2"}}}""", "charge.dispute.create", "R2")]
    public void WebhookEvent_ParsesTolerantly(string json, string expectedEvent, string? expectedReference)
    {
        var evt = PaystackWebhookEvent.TryParse(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(evt);
        Assert.Equal(expectedEvent, evt.Event);
        Assert.Equal(expectedReference, evt.Reference);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("""{"data":{}}""")]
    [InlineData("{")]
    public void WebhookEvent_RejectsNonEvents(string json) =>
        Assert.Null(PaystackWebhookEvent.TryParse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void WebhookEvent_RejectsExcessiveNesting()
    {
        var deep = "{\"event\":\"x\",\"data\":" + string.Concat(Enumerable.Repeat("[", 40)) + string.Concat(Enumerable.Repeat("]", 40)) + "}";
        Assert.Null(PaystackWebhookEvent.TryParse(Encoding.UTF8.GetBytes(deep)));
    }

    // ---- Postgres (runs only with PLATFORM_TEST_DB) ----

    [Fact]
    public async Task Postgres_ConcurrentCheckoutClicks_ShareOneCheckout()
    {
        await using var pg = await PostgresBehaviorTests.TempDatabase.CreateAsync();
        if (pg is null)
            return;

        string invoiceId;
        await using (var seed = pg.NewContext())
            invoiceId = (await CreateInvoiceAsync(seed, CreateBilling(seed), 75m)).Id;

        var sessions = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var db = pg.NewContext();
            var (service, _) = CreateService(db);
            return await service.StartCheckoutAsync(invoiceId, "admin");
        }));

        Assert.Single(sessions.Select(s => s.Reference).Distinct());
        await using var check = pg.NewContext();
        Assert.Equal(1, await check.PaymentCheckouts.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Postgres_ConcurrentWebhooks_RecordOneReceipt()
    {
        await using var pg = await PostgresBehaviorTests.TempDatabase.CreateAsync();
        if (pg is null)
            return;

        string invoiceId, reference;
        await using (var seed = pg.NewContext())
        {
            invoiceId = (await CreateInvoiceAsync(seed, CreateBilling(seed), 20m)).Id;
            var (service, _) = CreateService(seed);
            reference = (await service.StartCheckoutAsync(invoiceId, "admin")).Reference;
        }

        await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
        {
            await using var db = pg.NewContext();
            var (service, gateway) = CreateService(db);
            gateway.Verification = new GatewayVerification(GatewayTransactionState.Success, 2000, "GHS", "card", DateTime.UtcNow, "1", null);
            await service.HandleWebhookAsync(WebhookBody(reference));
        }));

        await using var check = pg.NewContext();
        Assert.Single(await check.Receipts.IgnoreQueryFilters().Where(r => r.InvoiceId == invoiceId).ToListAsync());
        Assert.Equal(PaymentCheckoutStatus.Paid, (await check.PaymentCheckouts.IgnoreQueryFilters().SingleAsync()).Status);
        Assert.Equal(InvoiceStatus.Paid, (await check.Invoices.IgnoreQueryFilters().SingleAsync(i => i.Id == invoiceId)).Status);
    }

    // ---- helpers ----

    private static byte[] WebhookBody(string reference) =>
        Encoding.UTF8.GetBytes($$$"""{"event":"charge.success","data":{"reference":"{{{reference}}}","amount":1,"status":"success"}}""");

    private static PaystackGateway CreateGateway(HttpMessageHandler handler) =>
        new(new HttpClient(handler), Options.Create(new PaystackOptions { SecretKey = Secret }), NullLogger<PaystackGateway>.Instance);

    private static (PaymentCheckoutService Service, FakeGateway Gateway) CreateService(AppDbContext db, string? secretKey = Secret)
    {
        var gateway = new FakeGateway();
        var service = new PaymentCheckoutService(
            db,
            CreateBilling(db),
            gateway,
            Options.Create(new PaystackOptions { SecretKey = secretKey }),
            Options.Create(new AuthSettings { ClientBaseUrl = "https://helmutcode.runasp.net/" }),
            NullLogger<PaymentCheckoutService>.Instance);
        return (service, gateway);
    }

    private static PaymentsController CreateController(IPaymentCheckoutService service, string? secretKey, string body, string? signature)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (signature is not null)
            context.Request.Headers[PaystackSignature.HeaderName] = signature;
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        return new PaymentsController(
            service,
            Options.Create(new PaystackOptions { SecretKey = secretKey }),
            NullLogger<PaymentsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static BillingService CreateBilling(AppDbContext db) =>
        new(db, new NoopAudit(), new EmailOutboxService(db), new EmailTemplateService(), new NoopDenyList());

    private static async Task<InvoiceDto> CreateInvoiceAsync(AppDbContext db, BillingService billing, decimal subtotal, string currency = "GHS")
    {
        var customer = await db.Customers.FirstOrDefaultAsync();
        if (customer is null)
        {
            customer = new Customer { Name = "Acme Ltd", ContactEmail = "billing@acme.test" };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
        }

        return await billing.CreateInvoiceAsync(new CreateInvoiceRequest
        {
            CustomerId = customer.Id,
            Status = InvoiceStatus.Sent,
            Currency = currency,
            Subtotal = subtotal,
            TaxAmount = 0m
        }, "admin");
    }

    private static AppDbContext CreateDbContext(string? name = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name ?? NUlid.Ulid.NewUlid().ToString())
            .Options);

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw ex;
        }
    }

    private sealed class FakeGateway : IPaymentGateway
    {
        public List<GatewayCheckoutRequest> Initialized { get; } = [];
        public GatewayVerification Verification { get; set; } =
            new(GatewayTransactionState.Pending, 0, null, null, null, null, "ongoing");
        public Dictionary<string, GatewayVerification> VerifyByReference { get; } = [];
        public PaymentGatewayException? VerifyError { get; set; }
        public int VerifyCalls { get; private set; }

        public Task<GatewayCheckout> InitializeAsync(GatewayCheckoutRequest request, CancellationToken cancellationToken = default)
        {
            Initialized.Add(request);
            return Task.FromResult(new GatewayCheckout($"https://checkout.paystack.com/{request.Reference}", "code"));
        }

        public Task<GatewayVerification> VerifyAsync(string reference, CancellationToken cancellationToken = default)
        {
            VerifyCalls++;
            if (VerifyError is not null)
                throw VerifyError;
            return Task.FromResult(VerifyByReference.TryGetValue(reference, out var v) ? v : Verification);
        }
    }

    private sealed class StubHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class NoopAudit : IAuditLogService
    {
        public Task<IReadOnlyList<AuditLogDto>> ListAsync(string? customerId = null, string? licenseId = null,
            AuditAction? action = null, int limit = 100, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuditLogDto>>([]);

        public Task WriteAsync(AuditAction action, string performedBy, string? customerId = null, string? licenseId = null,
            string? invoiceId = null, string? detailsJson = null, string? ipAddress = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopDenyList : ILicenseDenyListService
    {
        public Task DenyLicenseAsync(string licenseId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DenyCustomerLicensesAsync(string customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsDeniedAsync(string licenseId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ClearLicenseDenyAsync(string licenseId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearCustomerDenyAsync(string customerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
