using MediatR;
using Microsoft.Extensions.Logging;
using TicketHub.Application.Abstractions;
using TicketHub.Domain.Orders;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.Application.Orders;

// Huỷ một đơn đã hết hạn giữ chỗ và trả vé về kho. Gọi lặp lại không trả vé hai lần: lần sau nhận NotExpirable.
public sealed record ExpireOrderCommand(Guid OrderId) : IRequest<Result>;

public sealed class ExpireOrderCommandHandler(
    TimeProvider timeProvider,
    IOrderRepository orderRepository,
    ITicketTypeRepository ticketTypeRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<ExpireOrderCommand, Result>
{
    public async Task<Result> Handle(ExpireOrderCommand request, CancellationToken cancellationToken)
    {
        var order = await orderRepository.GetByIdAsync(request.OrderId, cancellationToken);
        if (order is null)
        {
            return OrderErrors.NotFound(request.OrderId);
        }

        // Chép giá trị thường ra trước, giống CancelOrder: transaction có thể chạy lại và xoá entity khỏi tracker.
        var lines = order.Items.Select(i => (i.TicketTypeId, i.Quantity)).ToList();
        var now = timeProvider.GetUtcNow();

        return await unitOfWork.ExecuteInTransactionAsync<Result>(async ct =>
        {
            if (!await orderRepository.TryExpirePendingAsync(request.OrderId, now, ct))
            {
                return OrderErrors.NotExpirable;
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

// Quét các đơn đã hết hạn và gọi ExpireOrderCommand cho từng đơn. Một đơn lỗi được ghi log rồi bỏ qua, không chặn các đơn sau.
public sealed record ExpireDueOrdersCommand(int BatchSize) : IRequest<Result<int>>;

public sealed class ExpireDueOrdersCommandHandler(
    TimeProvider timeProvider,
    IOrderRepository orderRepository,
    ISender sender,
    ILogger<ExpireDueOrdersCommandHandler> logger) : IRequestHandler<ExpireDueOrdersCommand, Result<int>>
{
    public async Task<Result<int>> Handle(ExpireDueOrdersCommand request, CancellationToken cancellationToken)
    {
        var ids = await orderRepository.GetExpiredPendingIdsAsync(timeProvider.GetUtcNow(), request.BatchSize, cancellationToken);

        var expired = 0;
        foreach (var id in ids)
        {
            try
            {
                var result = await sender.Send(new ExpireOrderCommand(id), cancellationToken);
                if (result.IsSuccess)
                {
                    expired++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to expire order {OrderId}; continuing with the remaining orders.", id);
            }
        }

        return expired;
    }
}
