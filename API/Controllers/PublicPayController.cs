using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Platform.Api.Extensions;
using Platform.Api.Services.Renewals;
using Platform.Shared.Dtos.Payments;

namespace Platform.Api.Controllers;

/// <summary>Anonymous endpoints behind the emailed /pay/{token} link.</summary>
[AllowAnonymous]
[ApiController]
[Route("api/public/pay")]
[EnableRateLimiting(RateLimitingExtensions.PublicPayPolicy)]
[NoStore]
public class PublicPayController(PublicPayService pay) : ControllerBase
{
    [HttpGet("{token}")]
    public async Task<ActionResult<PublicInvoiceSummaryDto>> Get(string token, CancellationToken cancellationToken) =>
        Ok(await pay.GetSummaryAsync(token, cancellationToken));

    [HttpPost("{token}/checkout")]
    public async Task<ActionResult<CheckoutSessionDto>> Checkout(string token, CancellationToken cancellationToken) =>
        Ok(await pay.StartCheckoutAsync(token, cancellationToken));

    [HttpGet("{token}/status")]
    public async Task<ActionResult<PublicCheckoutStatusDto>> Status(
        string token, [FromQuery] string? reference, CancellationToken cancellationToken) =>
        Ok(await pay.GetCheckoutStatusAsync(token, reference, cancellationToken));

    /// <summary>Tokens are bearer secrets: never cache responses, never leak the URL in a Referer.</summary>
    private sealed class NoStoreAttribute : ResultFilterAttribute
    {
        public override void OnResultExecuting(ResultExecutingContext context)
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers["Referrer-Policy"] = "no-referrer";
            base.OnResultExecuting(context);
        }
    }
}
