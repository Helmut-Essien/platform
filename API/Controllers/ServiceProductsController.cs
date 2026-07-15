using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Extensions;
using Platform.Api.Http;
using Platform.Api.Services;
using Platform.Shared.Dtos.ServiceProducts;

namespace Platform.Api.Controllers;

[ApiController]
[Authorize(Policy = PlatformAuthorizationPolicies.AdminOnly)]
[Route("api/[controller]")]
public class ServiceProductsController(IServiceProductService serviceProducts) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ServiceProductDto>>> List(CancellationToken cancellationToken)
    {
        var result = await serviceProducts.ListAsync(cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ServiceProductDto>> Get(string id, CancellationToken cancellationToken)
    {
        var product = await serviceProducts.GetAsync(id, cancellationToken);
        return product is null ? NotFound() : Ok(product);
    }

    [HttpPost]
    public async Task<ActionResult<ServiceProductDto>> Create(
        [FromBody] CreateServiceProductRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var product = await serviceProducts.CreateAsync(
                request,
                AdminRequestContext.GetPerformedBy(HttpContext),
                AdminRequestContext.GetIpAddress(HttpContext),
                cancellationToken);

            return CreatedAtAction(nameof(Get), new { id = product.Id }, product);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ServiceProductDto>> Update(
        string id,
        [FromBody] UpdateServiceProductRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var product = await serviceProducts.UpdateAsync(
                id,
                request,
                AdminRequestContext.GetPerformedBy(HttpContext),
                AdminRequestContext.GetIpAddress(HttpContext),
                cancellationToken);

            return Ok(product);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        try
        {
            await serviceProducts.DeleteAsync(
                id,
                AdminRequestContext.GetPerformedBy(HttpContext),
                AdminRequestContext.GetIpAddress(HttpContext),
                cancellationToken);

            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
