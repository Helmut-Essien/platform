using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Extensions;
using Platform.Api.Http;
using Platform.Api.Services.Payments;
using Platform.Shared.Dtos.Payments;

namespace Platform.Api.Controllers;

[ApiController]
[Authorize(Policy = PlatformAuthorizationPolicies.AdminOnly)]
public class PaymentsController(
    IPaymentCheckoutService checkouts,
    IOptions<PaystackOptions> paystack,
    ILogger<PaymentsController> logger) : ControllerBase
{
    private const int MaxWebhookBytes = 64 * 1024;

    /// <summary>Whether online payments are enabled, and collections against the starter cap.</summary>
    [HttpGet("api/payments/status")]
    public async Task<ActionResult<OnlinePaymentsStatusDto>> Status(CancellationToken cancellationToken) =>
        Ok(await checkouts.GetStatusAsync(cancellationToken));

    [HttpPost("api/invoices/{invoiceId}/checkout")]
    public async Task<ActionResult<CheckoutSessionDto>> StartCheckout(string invoiceId, CancellationToken cancellationToken) =>
        Ok(await checkouts.StartCheckoutAsync(invoiceId, AdminRequestContext.GetPerformedBy(HttpContext), cancellationToken));

    /// <summary>Verifies the latest (or the given) checkout with Paystack on demand.</summary>
    [HttpGet("api/invoices/{invoiceId}/checkout/status")]
    public async Task<ActionResult<CheckoutStatusDto>> CheckoutStatus(
        string invoiceId,
        [FromQuery] string? reference,
        CancellationToken cancellationToken) =>
        Ok(await checkouts.GetCheckoutStatusAsync(invoiceId, reference, cancellationToken));

    /// <summary>
    /// Paystack webhook. The signature (HMAC-SHA512 of the raw body) is checked first; valid events
    /// always get 200 (processing errors are retried by the reconcile worker), invalid ones 401.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("api/payments/paystack/webhook")]
    [EnableRateLimiting(RateLimitingExtensions.PaymentWebhookPolicy)]
    [RequestSizeLimit(MaxWebhookBytes)]
    public async Task<IActionResult> PaystackWebhook(CancellationToken cancellationToken)
    {
        var options = paystack.Value;
        if (!options.Enabled)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Online payments are not configured." });

        byte[] body;
        using (var buffer = new MemoryStream())
        {
            await Request.Body.CopyToAsync(buffer, cancellationToken);
            body = buffer.ToArray();
        }

        if (!PaystackSignature.IsValid(options.SecretKey, body, Request.Headers[PaystackSignature.HeaderName].FirstOrDefault()))
        {
            logger.LogWarning("Paystack webhook rejected: invalid signature from {Ip}.", AdminRequestContext.GetIpAddress(HttpContext));
            return Unauthorized();
        }

        try
        {
            // Bounded so Paystack gets its 200 quickly even if verify is slow; the worker retries.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await checkouts.HandleWebhookAsync(body, timeout.Token);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Paystack webhook processing failed; the reconcile worker will retry.");
        }

        return Ok();
    }
}
