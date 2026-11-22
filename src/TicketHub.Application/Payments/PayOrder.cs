using FluentValidation;
using MediatR;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Common.Exceptions;
using TicketHub.Domain.Orders;
using TicketHub.Domain.Payments;
using TicketHub.Domain.Shared;

namespace TicketHub.Application.Payments;

public sealed record PayOrderCommand(Guid OrderId) : IRequest<Result<PayOrderResult>>;

/// <param name="PaymentId">Id lượt thanh toán.</param>
/// <param name="ProviderTransactionId">Mã giao dịch phía cổng; dùng để dựng link thanh toán.</param>
/// <param name="Amount">Số tiền phải trả (lấy từ đơn).</param>
/// <param name="Created">false khi đơn đã có lượt Pending từ trước và trả lại lượt đó.</param>
public sealed record PayOrderResult(Guid PaymentId, string ProviderTransactionId, decimal Amount, bool Created);

public sealed class PayOrderCommandValidator : AbstractValidator<PayOrderCommand>
{
    public PayOrderCommandValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
    }
}

public sealed class PayOrderCommandHandler(
    ICurrentUser currentUser,
    IOrderRepository orderRepository,
    IPaymentRepository paymentRepository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : IRequestHandler<PayOrderCommand, Result<PayOrderResult>>
{
    // Unique index một phần (order_id) WHERE status = 'Pending'. PaymentConfiguration đặt tên index bằng chính hằng này.
    public const string PendingPaymentIndex = "ix_payments_order_id_pending";

    public async Task<Result<PayOrderResult>> Handle(PayOrderCommand request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(currentUser.UserId, out var userId))
        {
            return Error.Unauthorized("Orders.Unauthorized", "The user is not signed in.");
        }

        var order = await orderRepository.GetByIdAsync(request.OrderId, cancellationToken);

        // Đơn của người khác trả 404 giống đơn không tồn tại.
        if (order is null || order.UserId != userId)
        {
            return OrderErrors.NotFound(request.OrderId);
        }

        // Đã quá hạn giữ chỗ thì không mở thanh toán mới, dù job hết hạn chưa kịp chạy.
        if (order.Status != OrderStatus.Pending || timeProvider.GetUtcNow() >= order.ExpiresAt)
        {
            return OrderErrors.NotPayable;
        }

        // Bấm "Thanh toán" lần nữa khi lượt trước còn chờ: trả lại lượt đó, không mở thêm.
        if (await paymentRepository.GetPendingByOrderIdAsync(order.Id, cancellationToken) is { } pending)
        {
            return ToResult(pending, created: false);
        }

        // Số tiền lấy từ đơn ở server, không nhận từ client.
        var payment = Payment.CreateMock(order.Id, order.TotalAmount);
        paymentRepository.Add(payment);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return ToResult(payment, created: true);
        }
        catch (UniqueConstraintException ex) when (ex.ConstraintName == PendingPaymentIndex)
        {
            // Hai lần bấm cùng lúc đều qua bước kiểm ở trên; index chặn lần lưu sau → trả lượt của lần kia.
            var winner = await paymentRepository.GetPendingByOrderIdAsync(order.Id, cancellationToken)
                ?? throw new InvalidOperationException($"Pending payment conflict for order {order.Id} but none was found.", ex);
            return ToResult(winner, created: false);
        }
    }

    private static PayOrderResult ToResult(Payment payment, bool created) =>
        new(payment.Id, payment.ProviderTransactionId, payment.Amount, created);
}
