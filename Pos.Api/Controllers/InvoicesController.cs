using MediatR;
using Microsoft.AspNetCore.Mvc;
using Pos.Api.Features.Invoices;

namespace Pos.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class InvoicesController : ControllerBase
{
    private readonly IMediator _mediator;

    public InvoicesController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("finalize")]
    public async Task<IActionResult> Finalize([FromBody] FinalizeInvoiceRequest request, CancellationToken ct)
    {
        var command = FinalizeInvoiceCommand.FromRequest(request);
        var response = await _mediator.Send(command, ct);
        return Ok(response);
    }

    [HttpPost("returns")]
    public async Task<IActionResult> Return([FromBody] ReturnInvoiceItemRequest request, CancellationToken ct)
    {
        var command = ReturnInvoiceItemCommand.FromRequest(request);
        var response = await _mediator.Send(command, ct);
        return Ok(response);
    }
}

