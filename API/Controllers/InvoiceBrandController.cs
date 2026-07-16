using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Extensions;
using Platform.Api.Services;
using Platform.Shared.Dtos.Settings;

namespace Platform.Api.Controllers;

[ApiController]
[Authorize(Policy = PlatformAuthorizationPolicies.AdminOnly)]
[Route("api/invoice-brand")]
public class InvoiceBrandController(IInvoiceBrandService invoiceBrand) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<InvoiceBrandDto>> Get(CancellationToken cancellationToken)
    {
        var profile = await invoiceBrand.GetAsync(cancellationToken);
        return Ok(profile);
    }

    [HttpGet("logo")]
    public async Task<IActionResult> GetLogo(CancellationToken cancellationToken)
    {
        var logo = await invoiceBrand.GetLogoAsync(cancellationToken);
        if (logo is null)
            return NotFound();

        return File(logo.Value.Bytes, logo.Value.ContentType);
    }

    [HttpPut]
    public async Task<ActionResult<InvoiceBrandDto>> Update(
        [FromBody] UpdateInvoiceBrandRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var profile = await invoiceBrand.UpdateAsync(request, cancellationToken);
            return Ok(profile);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
