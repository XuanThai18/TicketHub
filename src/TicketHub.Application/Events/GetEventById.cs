using MediatR;
using Microsoft.EntityFrameworkCore;
using TicketHub.Application.Abstractions;
using TicketHub.Domain.Common;
using TicketHub.Domain.Events;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.Application.Events;

public sealed record EventDetailResponse(
    Guid Id,
    string Name,
    string? Description,
    string Venue,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset SaleStartsAt,
    DateTimeOffset SaleEndsAt,
    string Status,
    IReadOnlyList<TicketTypeResponse> TicketTypes);

public sealed record TicketTypeResponse(
    Guid Id,
    string Name,
    string? Description,
    decimal Price,
    int AvailableQuantity,
    int MaxQuantityPerOrder);

public sealed record GetEventByIdQuery(Guid Id) : IRequest<Result<EventDetailResponse>>;

public sealed class GetEventByIdQueryHandler(IReadDbContext readDbContext, ICurrentUser currentUser) : IRequestHandler<GetEventByIdQuery, Result<EventDetailResponse>>
{
    public async Task<Result<EventDetailResponse>> Handle(GetEventByIdQuery request, CancellationToken cancellationToken)
    {
        // bản nháp chỉ Admin thấy, còn bản công khai thì ai cũng thấy
        var canSeeDraft = currentUser.IsInRole(Roles.Admin);

        var ev = await readDbContext.Query<Event>()
                .Where(e => e.Id == request.Id && (canSeeDraft || e.Status != EventStatus.Draft))
                .Select(e => new
                {
                    e.Id,
                    e.Name,
                    e.Description,
                    e.Venue,
                    e.StartsAt,
                    e.EndsAt,
                    e.SaleStartsAt,
                    e.SaleEndsAt,
                    Status = e.Status.ToString()
                })
                .FirstOrDefaultAsync(cancellationToken);

        if (ev is null)
        {
            return EventErrors.NotFound(request.Id);
        }

        var ticketTypes = await readDbContext.Query<TicketType>()
            .Where(t => t.EventId == request.Id)
            .OrderBy(t => t.Price)
            .Select(t => new TicketTypeResponse(
                t.Id,
                t.Name,
                t.Description,
                t.Price,
                t.AvailableQuantity,
                t.MaxQuantityPerOrder))
            .ToListAsync(cancellationToken);

        return new EventDetailResponse(
            ev.Id,
            ev.Name,
            ev.Description,
            ev.Venue,
            ev.StartsAt,
            ev.EndsAt,
            ev.SaleStartsAt,
            ev.SaleEndsAt,
            ev.Status,
            ticketTypes);
    }
}
