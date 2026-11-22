using NSubstitute;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Common.Exceptions;
using TicketHub.Application.Payments;
using TicketHub.Domain.Orders;
using TicketHub.Domain.Payments;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.UnitTests.Application;

public sealed class PayOrderTests
{
    private static readonly DateTimeOffset _created = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid _userId = Guid.NewGuid();

    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IPaymentRepository _payments = Substitute.For<IPaymentRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public PayOrderTests() => _currentUser.UserId.Returns(_userId.ToString());

    // Mặc định "bây giờ" là 1 phút sau khi đặt: đơn còn 9 phút giữ chỗ.
    private PayOrderCommandHandler Handler(DateTimeOffset? now = null) =>
        new(_currentUser, _orders, _payments, _unitOfWork, new FixedTimeProvider(now ?? _created.AddMinutes(1)));

    private Order GivenOrder(Guid? ownerId = null)
    {
        var vip = TicketType.Create(Guid.NewGuid(), "VIP", null, 500_000m, totalQuantity: 10, maxQuantityPerOrder: 4).Value;
        var order = Order.Create(ownerId ?? _userId, [(vip, 2)], _created).Value;
        _orders.GetByIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns(order);
        return order;
    }

    [Fact]
    public async Task Handle_PendingOrder_CreatesPaymentForOrderTotal()
    {
        var order = GivenOrder();

        var result = await Handler().Handle(new PayOrderCommand(order.Id), CancellationToken.None);

        Assert.True(result.Value.Created);
        Assert.Equal(1_000_000m, result.Value.Amount); // lấy từ đơn, không phải từ client
        _payments.Received(1).Add(Arg.Is<Payment>(p =>
            p.OrderId == order.Id && p.Amount == 1_000_000m && p.Status == PaymentStatus.Pending));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PendingPaymentExists_ReturnsItWithoutCreating()
    {
        var order = GivenOrder();
        var existing = Payment.CreateMock(order.Id, order.TotalAmount);
        _payments.GetPendingByOrderIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns(existing);

        var result = await Handler().Handle(new PayOrderCommand(order.Id), CancellationToken.None);

        Assert.Equal(new PayOrderResult(existing.Id, existing.ProviderTransactionId, existing.Amount, Created: false), result.Value);
        _payments.DidNotReceive().Add(Arg.Any<Payment>());
    }

    [Fact]
    public async Task Handle_ConcurrentPayCreatedPendingFirst_ReturnsThatPayment()
    {
        // Hai lần bấm cùng lúc: lúc kiểm chưa có lượt nào, lúc lưu thì lượt kia đã có → index một phần chặn.
        var order = GivenOrder();
        var winner = Payment.CreateMock(order.Id, order.TotalAmount);
        _payments.GetPendingByOrderIdAsync(order.Id, Arg.Any<CancellationToken>()).Returns(null, winner);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromException<int>(
            new UniqueConstraintException("duplicate") { ConstraintName = PayOrderCommandHandler.PendingPaymentIndex }));

        var result = await Handler().Handle(new PayOrderCommand(order.Id), CancellationToken.None);

        Assert.Equal(winner.Id, result.Value.PaymentId);
        Assert.False(result.Value.Created);
    }

    [Fact]
    public async Task Handle_OtherUsersOrder_ReturnsNotFound()
    {
        var order = GivenOrder(ownerId: Guid.NewGuid());

        var result = await Handler().Handle(new PayOrderCommand(order.Id), CancellationToken.None);

        Assert.Equal(OrderErrors.NotFound(order.Id), result.Error);
        _payments.DidNotReceive().Add(Arg.Any<Payment>());
    }

    [Fact]
    public async Task Handle_PastExpiresAtEvenIfStillPending_ReturnsNotPayable()
    {
        // Job hết hạn chưa kịp chạy nên đơn vẫn Pending, nhưng đã quá 10 phút: không mở thanh toán mới.
        var order = GivenOrder();

        var result = await Handler(now: order.ExpiresAt).Handle(new PayOrderCommand(order.Id), CancellationToken.None);

        Assert.Equal(OrderErrors.NotPayable, result.Error);
        _payments.DidNotReceive().Add(Arg.Any<Payment>());
    }

    [Fact]
    public async Task Handle_UserNotSignedIn_ReturnsUnauthorized()
    {
        _currentUser.UserId.Returns((string?)null);

        var result = await Handler().Handle(new PayOrderCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal("Orders.Unauthorized", result.Error.Code);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
