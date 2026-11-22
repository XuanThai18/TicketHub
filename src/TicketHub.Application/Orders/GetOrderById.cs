using MediatR;
using Microsoft.EntityFrameworkCore;
using TicketHub.Application.Abstractions;
using TicketHub.Domain.Common;
using TicketHub.Domain.Events;
using TicketHub.Domain.OrderItems;
using TicketHub.Domain.Orders;
using TicketHub.Domain.Shared;
using TicketHub.Domain.Tickets;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.Application.Orders;

public sealed record OrderDetailResponse(
    Guid Id,
    string Code,
    Guid UserId,
    Guid EventId,
    string? EventName,
    string Status,
    decimal TotalAmount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? PaidAt,
    IReadOnlyList<OrderItemResponse> Items,
    IReadOnlyList<OrderTicketResponse> Tickets);

/// <summary>Vé đã phát cho đơn; rỗng khi đơn chưa thanh toán.</summary>
public sealed record OrderTicketResponse(Guid Id, string Code, Guid TicketTypeId, string Status, DateTimeOffset IssuedAt);

public sealed record OrderItemResponse(
    Guid TicketTypeId,
    string? TicketTypeName,
    int Quantity,
    decimal UnitPrice,
    decimal Subtotal);

public sealed record GetOrderByIdQuery(Guid Id) : IRequest<Result<OrderDetailResponse>>;

public sealed class GetOrderByIdQueryHandler(IReadDbContext readDbContext, ICurrentUser currentUser)
    : IRequestHandler<GetOrderByIdQuery, Result<OrderDetailResponse>>
{
    public async Task<Result<OrderDetailResponse>> Handle(GetOrderByIdQuery request, CancellationToken cancellationToken)
    {
        var isAdmin = currentUser.IsInRole(Roles.Admin);
        if (!Guid.TryParse(currentUser.UserId, out var userId) && !isAdmin)
        {
            return Error.Unauthorized("Orders.Unauthorized", "The user is not signed in.");
        }

        // Đơn của người khác trả 404 giống đơn không tồn tại (trừ Admin), để không lộ việc đơn đó có tồn tại.
        // Tên sự kiện lấy bằng truy vấn con: sự kiện đã xoá mềm thì tên là null nhưng đơn vẫn hiện.
        var order = await readDbContext.Query<Order>()
            .Where(o => o.Id == request.Id && (isAdmin || o.UserId == userId))
            .Select(o => new
            {
                o.Id,
                o.Code,
                o.UserId,
                o.EventId,
                EventName = readDbContext.Query<Event>().Where(e => e.Id == o.EventId).Select(e => e.Name).FirstOrDefault(),
                Status = o.Status.ToString(),
                o.TotalAmount,
                o.CreatedAt,
                o.ExpiresAt,
                o.PaidAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (order is null)
        {
            return OrderErrors.NotFound(request.Id);
        }

        var items = await readDbContext.Query<OrderItem>()
            .Where(i => i.OrderId == request.Id)
            .OrderBy(i => i.UnitPrice)
            .Select(i => new OrderItemResponse(
                i.TicketTypeId,
                readDbContext.Query<TicketType>().Where(t => t.Id == i.TicketTypeId).Select(t => t.Name).FirstOrDefault(),
                i.Quantity,
                i.UnitPrice,
                i.Quantity * i.UnitPrice))
            .ToListAsync(cancellationToken);

        var tickets = await readDbContext.Query<Ticket>()
            .Where(t => t.OrderId == request.Id)
            .OrderBy(t => t.Id)
            .Select(t => new OrderTicketResponse(t.Id, t.Code, t.TicketTypeId, t.Status.ToString(), t.IssuedAt))
            .ToListAsync(cancellationToken);

        return new OrderDetailResponse(
            order.Id,
            order.Code,
            order.UserId,
            order.EventId,
            order.EventName,
            order.Status,
            order.TotalAmount,
            order.CreatedAt,
            order.ExpiresAt,
            order.PaidAt,
            items,
            tickets);
    }
}
