using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Extensions;
using Platform.Api.Services.Email;
using Platform.Shared.Dtos.Email;

namespace Platform.Api.Controllers;

[ApiController]
[Authorize(Policy = PlatformAuthorizationPolicies.AdminOnly)]
[Route("api/email-deliveries")]
public class EmailDeliveriesController(IEmailOutboxService outbox) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<EmailDeliveryDto>>> List(
        [FromQuery] string? customerId,
        [FromQuery] string? licenseId,
        [FromQuery] string? invoiceId,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default) =>
        Ok(await outbox.ListAsync(customerId, licenseId, invoiceId, limit, cancellationToken));

    [HttpPost("{id}/retry")]
    public async Task<ActionResult<EmailDeliveryDto>> Retry(string id, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await outbox.RetryAsync(id, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
