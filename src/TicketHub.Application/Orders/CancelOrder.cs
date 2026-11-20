using FluentValidation;
using MediatR;
using TicketHub.Application.Abstractions;
using TicketHub.Domain.Orders;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.Application.Orders;

public sealed record CancelOrderCommand(Guid OrderId) : IRequest<Result>;

public sealed class CancelOrderCommandValidator : AbstractValidator<CancelOrderCommand>
{
    public CancelOrderCommandValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
    }
}

public sealed class CancelOrderCommandHandler(
    ICurrentUser currentUser,
    IOrderRepository orderRepository,
    ITicketTypeRepository ticketTypeRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<CancelOrderCommand, Result>
{
    public async Task<Result> Handle(CancelOrderCommand request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(currentUser.UserId, out var userId))
        {
            return Error.Unauthorized("Orders.Unauthorized", "The user is not signed in.");
        }

        var order = await orderRepository.GetByIdAsync(request.OrderId, cancellationToken);

        // Đơn của người khác trả 404 giống đơn không tồn tại, để không lộ việc đơn đó có tồn tại.
        if (order is null || order.UserId != userId)
        {
            return OrderErrors.NotFound(request.OrderId);
        }

        // Chép giá trị thường ra trước: nếu transaction phải chạy lại, entity cũ đã bị xoá khỏi tracker.
        var lines = order.Items.Select(i => (i.TicketTypeId, i.Quantity)).ToList();

        // Một transaction gồm hai bước. Bước 1 quyết định ai được huỷ: chỉ luồng đổi được Pending thì được trả vé.
        // Bước 2 cộng vé bằng UPDATE tại DB. Nếu bước 2 lỗi, transaction rollback cả bước 1.
        return await unitOfWork.ExecuteInTransactionAsync<Result>(async ct =>
        {
            if (!await orderRepository.TryCancelPendingAsync(request.OrderId, ct))
            {
                return OrderErrors.NotCancellable;
            }

            foreach (var (ticketTypeId, quantity) in lines)
            {
                if (!await ticketTypeRepository.TryReturnStockAsync(ticketTypeId, quantity, ct))
                {
                    return TicketTypeErrors.UnreserveExceedsTotal;
                }
            }

            return Result.Success();
        }, cancellationToken: cancellationToken);
    }
}
