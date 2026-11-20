using System.Data;
using NSubstitute;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Orders;
using TicketHub.Domain.Orders;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.UnitTests.Application;

public sealed class ExpireOrderTests
{
    private static readonly DateTimeOffset _now = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid _eventId = Guid.NewGuid();

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly ITicketTypeRepository _ticketTypes = Substitute.For<ITicketTypeRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public ExpireOrderTests()
    {
        // Transaction chạy thẳng delegate, để test thấy kết quả của bên trong.
        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<Result>>>(), Arg.Any<IsolationLevel>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Func<CancellationToken, Task<Result>>>(0)(CancellationToken.None));

        // Mặc định: đơn đã hết hạn và cộng vé thành công.
        _orders.TryExpirePendingAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(true);
        _ticketTypes.TryReturnStockAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private ExpireOrderCommandHandler Handler() =>
        new(new FixedTimeProvider(_now), _orders, _ticketTypes, _unitOfWork);

    private static TicketType GivenTicket(string name = "VIP", int total = 10, int maxPerOrder = 4) =>
        TicketType.Create(_eventId, name, null, 500_000m, total, maxPerOrder).Value;

    // Trừ vé trước rồi dựng đơn Pending; chỉ để có Items, số vé thật nằm ở DB.
    private static Order GivenPendingOrder(params (TicketType Ticket, int Quantity)[] lines)
    {
        foreach (var (ticket, quantity) in lines)
        {
            Assert.True(ticket.Reserve(quantity).IsSuccess);
        }

        return Order.Create(Guid.NewGuid(), lines.ToList(), _now).Value;
    }

    private void GivenOrder(Order? order) =>
        _orders.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(order);

    [Fact]
    public async Task Handle_ExpiredOrder_ExpiresAndReturnsStock()
    {
        var vip = GivenTicket();
        var order = GivenPendingOrder((vip, 2));
        GivenOrder(order);

        var result = await Handler().Handle(new ExpireOrderCommand(order.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        await _orders.Received(1).TryExpirePendingAsync(order.Id, _now, Arg.Any<CancellationToken>());
        await _ticketTypes.Received(1).TryReturnStockAsync(vip.Id, 2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NotYetExpiredOrAlreadyHandled_ReturnsNotExpirableAndKeepsStock()
    {
        // Repository trả false khi đơn không còn Pending hoặc chưa hết hạn: không được trả vé.
        _orders.TryExpirePendingAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);
        var order = GivenPendingOrder((GivenTicket(), 2));
        GivenOrder(order);

        var result = await Handler().Handle(new ExpireOrderCommand(order.Id), CancellationToken.None);

        Assert.Equal(OrderErrors.NotExpirable, result.Error);
        await _ticketTypes.DidNotReceive().TryReturnStockAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_MultipleLines_ReturnsStockForEachLine()
    {
        var vip = GivenTicket("VIP");
        var normal = GivenTicket("Thường", total: 20);
        var order = GivenPendingOrder((vip, 2), (normal, 3));
        GivenOrder(order);

        var result = await Handler().Handle(new ExpireOrderCommand(order.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        await _ticketTypes.Received(1).TryReturnStockAsync(vip.Id, 2, Arg.Any<CancellationToken>());
        await _ticketTypes.Received(1).TryReturnStockAsync(normal.Id, 3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_StockReturnFails_ReturnsUnreserveExceedsTotal()
    {
        _ticketTypes.TryReturnStockAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);
        var order = GivenPendingOrder((GivenTicket(), 2));
        GivenOrder(order);

        var result = await Handler().Handle(new ExpireOrderCommand(order.Id), CancellationToken.None);

        Assert.Equal(TicketTypeErrors.UnreserveExceedsTotal, result.Error);
    }

    [Fact]
    public async Task Handle_OrderNotFound_ReturnsNotFound()
    {
        GivenOrder(null);
        var missingId = Guid.NewGuid();

        var result = await Handler().Handle(new ExpireOrderCommand(missingId), CancellationToken.None);

        Assert.Equal(OrderErrors.NotFound(missingId), result.Error);
        await _orders.DidNotReceive().TryExpirePendingAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
