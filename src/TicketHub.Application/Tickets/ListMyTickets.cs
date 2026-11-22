using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Common.Paging;
using TicketHub.Domain.Events;
using TicketHub.Domain.Orders;
using TicketHub.Domain.Shared;
using TicketHub.Domain.Tickets;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.Application.Tickets;

public sealed record MyTicketResponse(
    Guid Id,
    string Code,
    string Status,
    DateTimeOffset IssuedAt,
    Guid OrderId,
    string OrderCode,
    Guid EventId,
    string? EventName,
    DateTimeOffset? EventStartsAt,
    Guid TicketTypeId,
    string? TicketTypeName);

public sealed class ListMyTicketsQueryValidator : AbstractValidator<ListMyTicketsQuery>
{
    public ListMyTicketsQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed record ListMyTicketsQuery(int Page, int PageSize) : IRequest<Result<PagedResult<MyTicketResponse>>>;

public sealed class ListMyTicketsQueryHandler(IReadDbContext readDbContext, ICurrentUser currentUser)
    : IRequestHandler<ListMyTicketsQuery, Result<PagedResult<MyTicketResponse>>>
{
    public async Task<Result<PagedResult<MyTicketResponse>>> Handle(ListMyTicketsQuery request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(currentUser.UserId, out var userId))
        {
            return Error.Unauthorized("Tickets.Unauthorized", "The user is not signed in.");
        }

        // Vé thuộc về người mua đơn: join qua orders để chỉ lấy vé của chính mình.
        var query =
            from t in readDbContext.Query<Ticket>()
            join o in readDbContext.Query<Order>() on t.OrderId equals o.Id
            where o.UserId == userId
            select new { Ticket = t, Order = o };

        var totalCount = await query.CountAsync(cancellationToken);

        // Mới phát trước; Id (Guid v7) phá hoà giữa các vé cùng một lần phát.
        var tickets = await query
            .OrderByDescending(x => x.Ticket.IssuedAt)
            .ThenBy(x => x.Ticket.Id)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(x => new MyTicketResponse(
                x.Ticket.Id,
                x.Ticket.Code,
                x.Ticket.Status.ToString(),
                x.Ticket.IssuedAt,
                x.Order.Id,
                x.Order.Code,
                x.Order.EventId,
                readDbContext.Query<Event>().Where(e => e.Id == x.Order.EventId).Select(e => e.Name).FirstOrDefault(),
                readDbContext.Query<Event>().Where(e => e.Id == x.Order.EventId).Select(e => (DateTimeOffset?)e.StartsAt).FirstOrDefault(),
                x.Ticket.TicketTypeId,
                readDbContext.Query<TicketType>().Where(tt => tt.Id == x.Ticket.TicketTypeId).Select(tt => tt.Name).FirstOrDefault()))
            .ToListAsync(cancellationToken);

        return new PagedResult<MyTicketResponse>(
            Items: tickets,
            Page: request.Page,
            PageSize: request.PageSize,
            TotalCount: totalCount);
    }
}
