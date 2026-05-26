using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Platform.Api.Extensions;
using Platform.Api.Services;
using Platform.Shared.Dtos.Licenses;

namespace Platform.Api.Controllers;

[ApiController]
[AllowAnonymous]
[EnableRateLimiting(RateLimitingExtensions.LicenseValidatePolicy)]
[Route("api/licenses")]
public class LicenseValidationController(ILicenseValidationService validation) : ControllerBase
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
                Message = $"Header {IntegrationKeyHeader} is required."
            });
        }

        var result = await validation.ValidateAsync(integrationKey.ToString(), request, cancellationToken);
        return Ok(result);
    }
}
