using FluentValidation;
using MediatR;
using TicketHub.Application.Abstractions;
using TicketHub.Domain.Events;
using TicketHub.Domain.Orders;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.Application.Orders;

public sealed record PlaceOrderLine(Guid TicketTypeId, int Quantity);

public sealed record PlaceOrderCommand(IReadOnlyList<PlaceOrderLine> Lines) : IRequest<Result<Guid>>;

public sealed class PlaceOrderCommandValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderCommandValidator()
    {
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.TicketTypeId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
        });
    }
}

public sealed class PlaceOrderCommandHandler(
    ICurrentUser currentUser,
    IEventRepository eventRepository,
    ITicketTypeRepository ticketTypeRepository,
    IOrderRepository orderRepository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IRequestHandler<PlaceOrderCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(PlaceOrderCommand request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(currentUser.UserId, out var userId))
        {
            return Error.Unauthorized("Orders.Unauthorized", "The user is not signed in.");
        }

        var now = timeProvider.GetUtcNow();

        // 1. Tải tất cả loại vé trong một truy vấn, rồi kiểm từng dòng có tồn tại không.
        var ids = request.Lines.Select(l => l.TicketTypeId).Distinct().ToList();
        var ticketTypes = await ticketTypeRepository.GetByIdsAsync(ids, cancellationToken);
        var byId = ticketTypes.ToDictionary(t => t.Id);

        foreach (var line in request.Lines)
        {
            if (!byId.ContainsKey(line.TicketTypeId))
            {
                return Error.NotFound("TicketTypes.NotFound", $"Ticket type '{line.TicketTypeId}' was not found.");
            }
        }

        var lines = request.Lines
            .Select(l => (TicketType: byId[l.TicketTypeId], Quantity: l.Quantity))
            .ToList();

        // 2. Dựng đơn: kiểm luật thuần tuý (rỗng, trùng loại vé, khác sự kiện, số lượng). Chưa trừ vé.
        var created = Order.Create(userId, lines, now);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var order = created.Value;

        // 3. Sự kiện phải đang mở bán.
        var ev = await eventRepository.GetByIdAsync(order.EventId, cancellationToken);
        if (ev is null)
        {
            return EventErrors.NotFound(order.EventId);
        }

        if (ev.Status != EventStatus.Published || now < ev.SaleStartsAt || now >= ev.SaleEndsAt)
        {
            return OrderErrors.NotOnSale;
        }

        // 4. Trừ vé bằng UPDATE có điều kiện rồi lưu đơn, tất cả trong một transaction.
        //    Một dòng hết vé thì rollback cả những dòng đã trừ. Sắp theo Id để hai đơn nhiều loại vé
        //    luôn khoá dòng theo cùng thứ tự, không khoá chéo nhau (deadlock).
        var reservations = lines
            .Select(l => (TicketTypeId: l.TicketType.Id, l.Quantity))
            .OrderBy(l => l.TicketTypeId)
            .ToList();

        return await unitOfWork.ExecuteInTransactionAsync<Result<Guid>>(async ct =>
        {
            foreach (var (ticketTypeId, quantity) in reservations)
            {
                if (!await ticketTypeRepository.TryReserveStockAsync(ticketTypeId, quantity, ct))
                {
                    return TicketTypeErrors.SoldOut;
                }
            }

            // Add nằm trong delegate: nếu transaction chạy lại, tracker đã bị xoá nên phải thêm đơn lần nữa.
            orderRepository.Add(order);
            await unitOfWork.SaveChangesAsync(ct);
            return order.Id;
        }, cancellationToken: cancellationToken);
    }
}
