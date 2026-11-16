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
public class ServiceProductsController(IServiceProductService serviceProducts, IProductPlanService productPlans) : ControllerBase
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
        var product = await serviceProducts.CreateAsync(
                request,
                AdminRequestContext.GetPerformedBy(HttpContext),
                AdminRequestContext.GetIpAddress(HttpContext),
                cancellationToken);

        return CreatedAtAction(nameof(Get), new { id = product.Id }, product);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<ServiceProductDto>> Update(
        string id,
        [FromBody] UpdateServiceProductRequest request,
        CancellationToken cancellationToken)
    {
        var product = await serviceProducts.UpdateAsync(
                id,
                request,
                AdminRequestContext.GetPerformedBy(HttpContext),
                AdminRequestContext.GetIpAddress(HttpContext),
                cancellationToken);

        return Ok(product);
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        await serviceProducts.DeleteAsync(
                id,
                AdminRequestContext.GetPerformedBy(HttpContext),
                AdminRequestContext.GetIpAddress(HttpContext),
                cancellationToken);

        return NoContent();
    }

    [HttpGet("{id}/plans")]
    public async Task<ActionResult<IReadOnlyList<ProductPlanDto>>> ListPlans(string id, CancellationToken cancellationToken) =>
        Ok(await productPlans.ListAsync(id, cancellationToken));

    [HttpPost("{id}/plans")]
    public async Task<ActionResult<ProductPlanDto>> CreatePlan(
        string id,
        [FromBody] CreateProductPlanRequest request,
        CancellationToken cancellationToken)
    {
        var plan = await productPlans.CreateAsync(
            id, request, AdminRequestContext.GetPerformedBy(HttpContext), AdminRequestContext.GetIpAddress(HttpContext), cancellationToken);
        return CreatedAtAction(nameof(ListPlans), new { id }, plan);
    }

    [HttpPut("{id}/plans/{planId}")]
    public async Task<ActionResult<ProductPlanDto>> UpdatePlan(
        string id,
        string planId,
        [FromBody] UpdateProductPlanRequest request,
        CancellationToken cancellationToken) =>
        Ok(await productPlans.UpdateAsync(
            id, planId, request, AdminRequestContext.GetPerformedBy(HttpContext), AdminRequestContext.GetIpAddress(HttpContext), cancellationToken));

    [HttpDelete("{id}/plans/{planId}")]
    public async Task<ActionResult> DeletePlan(string id, string planId, CancellationToken cancellationToken)
    {
        await productPlans.DeleteAsync(
            id, planId, AdminRequestContext.GetPerformedBy(HttpContext), AdminRequestContext.GetIpAddress(HttpContext), cancellationToken);
        return NoContent();
    }

    /// <summary>Distinct plan names on this product's licenses, with counts (what still needs mapping).</summary>
    [HttpGet("{id}/plans/usage")]
    public async Task<ActionResult<IReadOnlyList<ProductPlanUsageDto>>> PlanUsage(string id, CancellationToken cancellationToken) =>
        Ok(await productPlans.GetUsageAsync(id, cancellationToken));

    /// <summary>One-off: move licenses from an old plan name onto a plan code. Send <c>dryRun: true</c> to preview.</summary>
    [HttpPost("{id}/plans/remap")]
    public async Task<ActionResult<RemapProductPlanResultDto>> RemapPlans(
        string id,
        [FromBody] RemapProductPlanRequest request,
        CancellationToken cancellationToken) =>
        Ok(await productPlans.RemapAsync(
            id, request, AdminRequestContext.GetPerformedBy(HttpContext), AdminRequestContext.GetIpAddress(HttpContext), cancellationToken));
}
