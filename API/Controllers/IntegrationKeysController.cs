using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Extensions;
using Platform.Api.Http;
using Platform.Api.Services;
using Platform.Shared.Dtos.IntegrationKeys;

namespace Platform.Api.Controllers;

[ApiController]
[Authorize(Policy = PlatformAuthorizationPolicies.AdminOnly)]
[Route("api/integration-keys")]
public class IntegrationKeysController(IIntegrationKeyService integrationKeys) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<IntegrationKeyDto>>> List(
        [FromQuery] string? serviceProductId,
        CancellationToken cancellationToken)
    {
        var keys = await integrationKeys.ListAsync(serviceProductId, cancellationToken);
        return Ok(keys);
    }

    [HttpPost]
    public async Task<ActionResult<CreateIntegrationKeyResponse>> Create(
        [FromQuery] string serviceProductId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await integrationKeys.CreateAsync(
                serviceProductId,
                AdminRequestContext.GetPerformedBy(HttpContext),
                AdminRequestContext.GetIpAddress(HttpContext),
                cancellationToken);

            return CreatedAtAction(nameof(List), new { serviceProductId }, result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/revoke")]
    public async Task<ActionResult<IntegrationKeyDto>> Revoke(string id, CancellationToken cancellationToken)
    {
        try
        {
            var key = await integrationKeys.RevokeAsync(
                id,
                AdminRequestContext.GetPerformedBy(HttpContext),
                AdminRequestContext.GetIpAddress(HttpContext),
                cancellationToken);

            return Ok(key);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
