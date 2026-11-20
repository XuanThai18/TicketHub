using System.Data;
using NSubstitute;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Orders;
using TicketHub.Domain.Orders;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.UnitTests.Application;

public sealed class CancelOrderTests
{
    private static readonly DateTimeOffset _now = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid _userId = Guid.NewGuid();
    private static readonly Guid _eventId = Guid.NewGuid();

    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ITicketTypeRepository _ticketTypes = Substitute.For<ITicketTypeRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public CancelOrderTests()
    {
        _currentUser.UserId.Returns(_userId.ToString());

        // Transaction chạy thẳng delegate, để test thấy kết quả của bên trong.
        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<Result>>>(), Arg.Any<IsolationLevel>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Func<CancellationToken, Task<Result>>>(0)(CancellationToken.None));

        // Mặc định: đơn còn Pending và cộng vé thành công.
        _orders.TryCancelPendingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _ticketTypes.TryReturnStockAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private CancelOrderCommandHandler Handler() =>
        new(_currentUser, _orders, _ticketTypes, _unitOfWork);

    private static TicketType GivenTicket(string name = "VIP", int total = 10, int maxPerOrder = 4) =>
        TicketType.Create(_eventId, name, null, 500_000m, total, maxPerOrder).Value;

    // Đơn đã giữ chỗ: trừ vé trước rồi dựng đơn Pending. Chỉ để có Items; số vé thật nằm ở DB.
    private static Order GivenPendingOrder(Guid userId, params (TicketType Ticket, int Quantity)[] lines)
    {
        foreach (var (ticket, quantity) in lines)
        {
            Assert.True(ticket.Reserve(quantity).IsSuccess);
        }

        return Order.Create(userId, lines.ToList(), _now).Value;
    }

    private void GivenOrder(Order? order)
    {
        _orders.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(order);
    }

    // ---------- Thành công ----------

    [Fact]
    public async Task Handle_OwnerCancelsPendingOrder_CancelsOrderReturnsStockAndCommits()
    {
        var vip = GivenTicket();
        var order = GivenPendingOrder(_userId, (vip, 2));
        GivenOrder(order);

        var result = await Handler().Handle(new CancelOrderCommand(order.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        await _orders.Received(1).TryCancelPendingAsync(order.Id, Arg.Any<CancellationToken>());
        await _ticketTypes.Received(1).TryReturnStockAsync(vip.Id, 2, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).ExecuteInTransactionAsync(
            Arg.Any<Func<CancellationToken, Task<Result>>>(), Arg.Any<IsolationLevel>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_MultipleLines_ReturnsStockForEachLine()
    {
        var vip = GivenTicket("VIP");
        var normal = GivenTicket("Thường", total: 20);
        var order = GivenPendingOrder(_userId, (vip, 3), (normal, 4));
        GivenOrder(order);

        var result = await Handler().Handle(new CancelOrderCommand(order.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        await _ticketTypes.Received(1).TryReturnStockAsync(vip.Id, 3, Arg.Any<CancellationToken>());
        await _ticketTypes.Received(1).TryReturnStockAsync(normal.Id, 4, Arg.Any<CancellationToken>());
    }

    // ---------- Quyền và đơn không tồn tại ----------

    [Fact]
    public async Task Handle_UserNotSignedIn_ReturnsUnauthorizedAndTouchesNothing()
    {
        _currentUser.UserId.Returns((string?)null);

        var result = await Handler().Handle(new CancelOrderCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal("Orders.Unauthorized", result.Error.Code);
        await _orders.DidNotReceive().TryCancelPendingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_OrderNotFound_ReturnsNotFoundAndTouchesNothing()
    {
        GivenOrder(null);

        var result = await Handler().Handle(new CancelOrderCommand(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal("Orders.NotFound", result.Error.Code);
        await _orders.DidNotReceive().TryCancelPendingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_OtherUsersOrder_ReturnsNotFoundAndTouchesNothing()
    {
        var vip = GivenTicket();
        var order = GivenPendingOrder(Guid.NewGuid(), (vip, 2));
        GivenOrder(order);

        var result = await Handler().Handle(new CancelOrderCommand(order.Id), CancellationToken.None);

        Assert.Equal(OrderErrors.NotFound(order.Id), result.Error);
        await _orders.DidNotReceive().TryCancelPendingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _ticketTypes.DidNotReceive().TryReturnStockAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ---------- Trạng thái và tranh chấp ----------

    [Fact]
    public async Task Handle_OrderNoLongerPending_ReturnsNotCancellableAndDoesNotReturnStock()
    {
        // Đã huỷ, đã hết hạn, hoặc luồng khác vừa huỷ xong: UPDATE có điều kiện trả 0 dòng.
        _orders.TryCancelPendingAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        var vip = GivenTicket();
        var order = GivenPendingOrder(_userId, (vip, 2));
        GivenOrder(order);

        var result = await Handler().Handle(new CancelOrderCommand(order.Id), CancellationToken.None);

        Assert.Equal(OrderErrors.NotCancellable, result.Error);
        await _ticketTypes.DidNotReceive().TryReturnStockAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_StockReturnFails_ReturnsErrorSoTransactionRollsBack()
    {
        // Lỗi ở bước trả vé phải trả Result thất bại để transaction rollback cả việc đổi trạng thái.
        _ticketTypes.TryReturnStockAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
        var vip = GivenTicket();
        var order = GivenPendingOrder(_userId, (vip, 2));
        GivenOrder(order);

        var result = await Handler().Handle(new CancelOrderCommand(order.Id), CancellationToken.None);

        Assert.Equal(TicketTypeErrors.UnreserveExceedsTotal, result.Error);
    }
}
