using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Common.Paging;
using TicketHub.Domain.Events;
using TicketHub.Domain.Orders;
using TicketHub.Domain.Shared;

namespace TicketHub.Application.Orders;

public sealed record OrderSummaryResponse(
    Guid Id,
    string Code,
    Guid EventId,
    string? EventName,
    string Status,
    decimal TotalAmount,
    int TicketCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

public sealed class ListMyOrdersQueryValidator : AbstractValidator<ListMyOrdersQuery>
{
    public ListMyOrdersQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
    }
}

public sealed record ListMyOrdersQuery(int Page, int PageSize) : IRequest<Result<PagedResult<OrderSummaryResponse>>>;

public sealed class ListMyOrdersQueryHandler(IReadDbContext readDbContext, ICurrentUser currentUser)
    : IRequestHandler<ListMyOrdersQuery, Result<PagedResult<OrderSummaryResponse>>>
{
    public async Task<Result<PagedResult<OrderSummaryResponse>>> Handle(ListMyOrdersQuery request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(currentUser.UserId, out var userId))
        {
            return Error.Unauthorized("Orders.Unauthorized", "The user is not signed in.");
        }

        var query = readDbContext.Query<Order>().Where(o => o.UserId == userId);

        var totalCount = await query.CountAsync(cancellationToken);

        // Mới nhất trước; Id (Guid v7, tăng theo thời gian) phá hoà khi hai đơn cùng CreatedAt.
        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(o => new OrderSummaryResponse(
                o.Id,
                o.Code,
                o.EventId,
                readDbContext.Query<Event>().Where(e => e.Id == o.EventId).Select(e => e.Name).FirstOrDefault(),
                o.Status.ToString(),
                o.TotalAmount,
                o.Items.Sum(i => i.Quantity),
                o.CreatedAt,
                o.ExpiresAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<OrderSummaryResponse>(
            Items: orders,
            Page: request.Page,
            PageSize: request.PageSize,
            TotalCount: totalCount);
    }
}
