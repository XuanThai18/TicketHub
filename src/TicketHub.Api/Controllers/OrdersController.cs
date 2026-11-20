using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TicketHub.Api.Contracts.Common;
using TicketHub.Api.Contracts.Orders;
using TicketHub.Application.Common.Paging;
using TicketHub.Application.Orders;
using TicketHub.Domain.Shared;

namespace TicketHub.Api.Controllers;

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/orders")]
public sealed class OrdersController(ISender sender) : BaseApiController
{
    [HttpPost]
    [Authorize]
    [ProducesResponseType<CreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PlaceOrder([FromBody] PlaceOrderRequest request, CancellationToken cancellationToken)
    {
        var lines = request.Lines
            .Select(l => new PlaceOrderLine(l.TicketTypeId, l.Quantity))
            .ToList();

        var result = await sender.Send(new PlaceOrderCommand(lines), cancellationToken);
        return CreatedOrProblem(result, id => $"/api/v1/orders/{id}");
    }

    [HttpGet("my")]
    [Authorize]
    [ProducesResponseType<PagedResult<OrderSummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListMyOrders(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 10, CancellationToken cancellationToken = default) =>
        HandleResult(await sender.Send(new ListMyOrdersQuery(page, pageSize), cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize]
    [ProducesResponseType<OrderDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrderById(Guid id, CancellationToken cancellationToken) =>
        HandleResult(await sender.Send(new GetOrderByIdQuery(id), cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelOrder(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new CancelOrderCommand(id), cancellationToken);
        return HandleResult(result);
    }

    private IActionResult CreatedOrProblem(Result<Guid> result, Func<Guid, string> location) =>
        result.IsFailure ? ToProblem(result.Error) : Created(location(result.Value), new CreatedResponse(result.Value));
}
