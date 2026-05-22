using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Extensions;
using Platform.Api.Http;
using Platform.Api.Services;
using Platform.Shared.Dtos.Licenses;

namespace Platform.Api.Controllers;

[ApiController]
[Authorize(Policy = PlatformAuthorizationPolicies.AdminOnly)]
[Route("api/[controller]")]
public class LicensesController(ILicenseService licenses) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<LicenseDto>>> List(
        [FromQuery] string? customerId,
        CancellationToken cancellationToken)
    {
        var result = await licenses.ListAsync(customerId, cancellationToken);
        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<LicenseDto>> Get(string id, CancellationToken cancellationToken)
    {
        var license = await licenses.GetAsync(id, cancellationToken);
        return license is null ? NotFound() : Ok(license);
    }

    [HttpPost]
    public async Task<ActionResult<LicenseDto>> Create(
        [FromBody] CreateLicenseRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var license = await licenses.CreateAsync(
                request,
                AdminRequestContext.GetPerformedBy(HttpContext),
                AdminRequestContext.GetIpAddress(HttpContext),
                cancellationToken);

            return CreatedAtAction(nameof(Get), new { id = license.Id }, license);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/activate")]
    public async Task<ActionResult<LicenseDto>> Activate(
        string id,
        [FromBody] ActivateLicenseRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var license = await licenses.ActivateAsync(
                id,
                request,
                AdminRequestContext.GetPerformedBy(HttpContext),
                AdminRequestContext.GetIpAddress(HttpContext),
                cancellationToken);

            return Ok(license);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id}/renew")]
    public async Task<ActionResult<LicenseDto>> Renew(
        string id,
        [FromBody] RenewLicenseRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var license = await licenses.RenewAsync(
                id,
                request,
                AdminRequestContext.GetPerformedBy(HttpContext),
                AdminRequestContext.GetIpAddress(HttpContext),
                cancellationToken);

            return Ok(license);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
