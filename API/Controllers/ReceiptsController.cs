using Microsoft.AspNetCore.Mvc;
using Platform.Api.Http;
using Platform.Api.Services;
using Platform.Shared.Dtos.Billing;

namespace Platform.Api.Controllers;

[ApiController]
[Route("api/invoices/{invoiceId}/receipts")]
public class ReceiptsController(IBillingService billing) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ReceiptDto>> Record(
        string invoiceId,
        [FromBody] RecordReceiptRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var receipt = await billing.RecordReceiptAsync(
                invoiceId,
                request,
                AdminRequestContext.GetPerformedBy(HttpContext),
                AdminRequestContext.GetIpAddress(HttpContext),
                cancellationToken);

            return CreatedAtAction(
                actionName: nameof(Record),
                controllerName: "Receipts",
                routeValues: new { invoiceId, id = receipt.Id },
                value: receipt);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
