using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Extensions;
using Platform.Api.Http;
using Platform.Api.Services;
using Platform.Shared.Dtos.Billing;
using Platform.Shared.Dtos.Common;
using Platform.Shared.Enums;

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
        [FromQuery] bool unpaidOnly = false,
        [FromQuery] InvoiceStatus? status = null,
        [FromQuery] string? search = null,
        [FromQuery] DateTime? issuedFrom = null,
        [FromQuery] DateTime? issuedTo = null,
        CancellationToken cancellationToken = default)
    {
        var invoices = await billing.ListInvoicesAsync(
            customerId, page, pageSize, unpaidOnly, status, search, issuedFrom, issuedTo, cancellationToken);
        return Ok(invoices);
    }

    /// <summary>Server-side aggregates for the invoices page (per currency, over all invoices).</summary>
    [HttpGet("stats")]
    public async Task<ActionResult<InvoiceStatsDto>> Stats(CancellationToken cancellationToken) =>
        Ok(await billing.GetInvoiceStatsAsync(cancellationToken));

    [HttpGet("{id}")]
    public async Task<ActionResult<InvoiceDto>> Get(string id, CancellationToken cancellationToken)
    {
        var invoice = await billing.GetInvoiceAsync(id, cancellationToken);
        return invoice is null ? NotFound() : Ok(invoice);
    }

    [HttpPost("{id}/send")]
    public async Task<ActionResult<InvoiceDto>> Send(string id, CancellationToken cancellationToken)
    {
        return Ok(await billing.SendInvoiceAsync(
                id,
                AdminRequestContext.GetPerformedBy(HttpContext),
                AdminRequestContext.GetIpAddress(HttpContext),
                cancellationToken));
    }

    [HttpPost]
    public async Task<ActionResult<InvoiceDto>> Create(
        [FromBody] CreateInvoiceRequest request,
        CancellationToken cancellationToken)
    {
        var invoice = await billing.CreateInvoiceAsync(
                request,
                AdminRequestContext.GetPerformedBy(HttpContext),
                AdminRequestContext.GetIpAddress(HttpContext),
                cancellationToken);

        return CreatedAtAction(nameof(Get), new { id = invoice.Id }, invoice);
    }

    [HttpPost("{id}/void")]
    public async Task<ActionResult<InvoiceDto>> Void(string id, CancellationToken cancellationToken)
    {
        var invoice = await billing.VoidInvoiceAsync(
                id,
                AdminRequestContext.GetPerformedBy(HttpContext),
                AdminRequestContext.GetIpAddress(HttpContext),
                cancellationToken);

        return Ok(invoice);
    }
}
