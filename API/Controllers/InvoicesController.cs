using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Extensions;
using Platform.Api.Http;
using Platform.Api.Services;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Dtos.Common;

namespace Platform.Api.Controllers;

[ApiController]
[Authorize(Policy = PlatformAuthorizationPolicies.AdminOnly)]
[Route("api/[controller]")]
public class InvoicesController(IBillingService billing) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<InvoiceDto>>> List(
        [FromQuery] string? customerId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var invoices = await billing.ListInvoicesAsync(customerId, page, pageSize, cancellationToken);
        return Ok(invoices);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<InvoiceDto>> Get(string id, CancellationToken cancellationToken)
    {
        var invoice = await billing.GetInvoiceAsync(id, cancellationToken);
        return invoice is null ? NotFound() : Ok(invoice);
    }

    [HttpPost]
    public async Task<ActionResult<InvoiceDto>> Create(
        [FromBody] CreateInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var invoice = await billing.CreateInvoiceAsync(
                request,
                AdminRequestContext.GetPerformedBy(HttpContext),
                AdminRequestContext.GetIpAddress(HttpContext),
                cancellationToken);

            return CreatedAtAction(nameof(Get), new { id = invoice.Id }, invoice);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/void")]
    public async Task<ActionResult<InvoiceDto>> Void(string id, CancellationToken cancellationToken)
    {
        try
        {
            var invoice = await billing.VoidInvoiceAsync(
                id,
                AdminRequestContext.GetPerformedBy(HttpContext),
                AdminRequestContext.GetIpAddress(HttpContext),
                cancellationToken);

            return Ok(invoice);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
