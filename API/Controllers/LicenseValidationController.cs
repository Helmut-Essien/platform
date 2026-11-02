using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Platform.Api.Configuration;
using Platform.Api.Extensions;
using Platform.Api.Security;
using Platform.Api.Services;
using Platform.Api.Services.Licensing;
using Platform.Shared.Dtos.Licenses;

namespace Platform.Api.Controllers;

/// <summary>
/// Public endpoints called by customer products. Contract:
/// 200 = the license was judged (isValid + reason); 400 = malformed request; 429/503 = not judged, retry after
/// the Retry-After header (and keep running on the last signed token while its grace lasts).
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingExtensions.LicenseValidatePolicy)]
[Route("api/licenses")]
public class LicenseValidationController(
    ILicenseValidationService validation,
    LicenseValidationRateLimiter rateLimiter,
    LicenseTokenSigner tokenSigner,
    IOptions<LicensingSettings> licensing) : ControllerBase
{
    public const string IntegrationKeyHeader = "X-Integration-Key";

    [HttpPost("validate")]
    public async Task<ActionResult<ValidateLicenseResponse>> Validate(
        [FromBody] ValidateLicenseRequest request,
        CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue(IntegrationKeyHeader, out var integrationKey) ||
            string.IsNullOrWhiteSpace(integrationKey))
        {
            return BadRequest(new ValidateLicenseResponse
            {
                IsValid = false,
                Reason = LicenseValidationReasons.InvalidRequest,
                Message = $"Header {IntegrationKeyHeader} is required.",
                ServerTime = DateTime.UtcNow
            });
        }

        if (rateLimiter.TryAcquire(HttpContext.Connection.RemoteIpAddress?.ToString(), request.LicenseKey, integrationKey.ToString().Trim()) is { } wait)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status429TooManyRequests, new ValidateLicenseErrorResponse
            {
                Reason = LicenseValidationReasons.RateLimited,
                Message = "Too many requests. Try again later.",
                ServerTime = DateTime.UtcNow,
                RetryAfterSeconds = seconds
            });
        }

        try
        {
            var result = await validation.ValidateAsync(integrationKey.ToString(), request, cancellationToken);
            Response.Headers.CacheControl = "no-store";
            return Ok(result);
        }
        catch (LicenseValidationUnavailableException)
        {
            var seconds = Math.Clamp(licensing.Value.UnavailableRetryAfterSeconds, 1, 3600);
            Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new ValidateLicenseErrorResponse
            {
                Reason = LicenseValidationReasons.ServiceUnavailable,
                Message = "License validation is temporarily unavailable. Keep using the last valid token and retry later.",
                ServerTime = DateTime.UtcNow,
                RetryAfterSeconds = seconds
            });
        }
    }

    /// <summary>Public keys (JWKS, RFC 7517) that verify license tokens. Products may cache this for a day.</summary>
    [HttpGet("keys")]
    [DisableRateLimiting]
    public IActionResult Keys()
    {
        Response.Headers.CacheControl = "public, max-age=3600";
        return Ok(new
        {
            keys = tokenSigner.PublicKeys.Select(k => new
            {
                kty = "EC",
                crv = "P-256",
                use = "sig",
                alg = LicenseTokenSigner.Algorithm,
                kid = k.Kid,
                x = k.X,
                y = k.Y
            })
        });
    }
}
