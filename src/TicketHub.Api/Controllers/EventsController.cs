using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TicketHub.Api.Contracts.Common;
using TicketHub.Api.Contracts.Events;
using TicketHub.Api.Extensions;
using TicketHub.Application.Events;
using TicketHub.Application.TicketTypes;
using TicketHub.Domain.Shared;

namespace TicketHub.Api.Controllers;

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/events")]
public sealed class EventsController(ISender sender) : BaseApiController
{
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType<EventDetailResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEventById([FromRoute] Guid id, CancellationToken cancellationToken)
        => HandleResult(await sender.Send(new GetEventByIdQuery(id), cancellationToken));

    [HttpPost]
    [Authorize(Policy = Policies.RequireManagerRole)]
    [ProducesResponseType<CreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateEvent([FromBody] CreateEventRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new CreateEventCommand(
            request.Name, request.Description, request.Venue,
            request.StartsAt, request.EndsAt, request.SaleStartsAt, request.SaleEndsAt), cancellationToken);
        return CreatedOrProblem(result, id => $"/api/v1/events/{id}");
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Policies.RequireManagerRole)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateEvent([FromRoute] Guid id, [FromBody] UpdateEventRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new UpdateEventCommand(
            id, request.Name, request.Description, request.Venue,
            request.StartsAt, request.EndsAt, request.SaleStartsAt, request.SaleEndsAt), cancellationToken);
        return HandleResult(result);
    }

    [HttpPost("{id:guid}/publish")]
    [Authorize(Policy = Policies.RequireManagerRole)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Publish(Guid id, CancellationToken cancellationToken) =>
        HandleResult(await sender.Send(new PublishEventCommand(id), cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = Policies.RequireAdminRole)] // huỷ sự kiện ảnh hưởng mọi người đã mua: chỉ Admin
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken) =>
        HandleResult(await sender.Send(new CancelEventCommand(id), cancellationToken));

    [HttpPost("{id:guid}/ticket-types")]
    [Authorize(Policy = Policies.RequireManagerRole)]
    [ProducesResponseType<CreatedResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddTicketType(Guid id, AddTicketTypeRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new AddTicketTypeCommand(
            id, request.Name, request.Description,
            request.Price, request.TotalQuantity, request.MaxQuantityPerOrder), cancellationToken);

        return CreatedOrProblem(result, _ => $"/api/v1/events/{id}");
    }

    [HttpPut("{id:guid}/ticket-types/{ticketTypeId:guid}")]
    [Authorize(Policy = Policies.RequireManagerRole)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateTicketType(
        Guid id, Guid ticketTypeId, UpdateTicketTypeRequest request, CancellationToken cancellationToken) =>
        HandleResult(await sender.Send(new UpdateTicketTypeCommand(
            id, ticketTypeId, request.Name, request.Description,
            request.Price, request.TotalQuantity, request.MaxQuantityPerOrder), cancellationToken));

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> ListEvents(
        [FromQuery] string? search,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
       => HandleResult(await sender.Send(new ListEventsQuery(search, page, pageSize, from, to), cancellationToken));

    private IActionResult CreatedOrProblem(Result<Guid> result, Func<Guid, string> location) =>
        result.IsFailure ? ToProblem(result.Error) : Created(location(result.Value), new CreatedResponse(result.Value));
}
