using NSubstitute;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Orders;
using TicketHub.Domain.Events;
using TicketHub.Domain.Orders;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.UnitTests.Application;

public sealed class PlaceOrderTests
{
    private static readonly DateTimeOffset _now = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid _userId = Guid.NewGuid();

    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IEventRepository _events = Substitute.For<IEventRepository>();
    private readonly ITicketTypeRepository _ticketTypes = Substitute.For<ITicketTypeRepository>();
    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public PlaceOrderTests()
    {
        _currentUser.UserId.Returns(_userId.ToString());
    }

    private PlaceOrderCommandHandler Handler() =>
        new(_currentUser, _events, _ticketTypes, _orders, _unitOfWork, new FixedTimeProvider(_now));

    // Sự kiện đang mở bán mặc định: từ hôm qua đến 10 ngày sau.
    private Event GivenEvent(bool publish = true, DateTimeOffset? saleStartsAt = null, DateTimeOffset? saleEndsAt = null)
    {
        var start = saleStartsAt ?? _now.AddDays(-1);
        var end = saleEndsAt ?? _now.AddDays(10);
        var ev = Event.Create("Concert", null, "Nhà hát",
            startsAt: end.AddDays(1), endsAt: end.AddDays(1).AddHours(3),
            saleStartsAt: start, saleEndsAt: end).Value;
        if (publish)
        {
            ev.Publish(ticketTypeCount: 1);
        }
        _events.GetByIdAsync(ev.Id, Arg.Any<CancellationToken>()).Returns(ev);
        return ev;
    }

    private static TicketType GivenTicket(Guid eventId, string name = "VIP", decimal price = 500_000m,
        int total = 10, int maxPerOrder = 4)
    {
        var ticket = TicketType.Create(eventId, name, null, price, total, maxPerOrder).Value;
        return ticket;
    }

    // Đăng ký các loại vé để repository trả về khi handler tải theo Id.
    private void GivenTicketsInStore(params TicketType[] tickets)
    {
        _ticketTypes.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(tickets);
    }

    private static PlaceOrderCommand CommandFor(params (TicketType Ticket, int Quantity)[] lines) =>
        new(lines.Select(l => new PlaceOrderLine(l.Ticket.Id, l.Quantity)).ToList());

    // ---------- Thành công ----------

    [Fact]
    public async Task Handle_ValidOrder_ReservesTicketsAddsOrderAndSaves()
    {
        var ev = GivenEvent();
        var vip = GivenTicket(ev.Id);
        GivenTicketsInStore(vip);

        var result = await Handler().Handle(CommandFor((vip, 2)), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(8, vip.AvailableQuantity);
        _orders.Received(1).Add(Arg.Is<Order>(o =>
            o.Id == result.Value && o.UserId == _userId && o.EventId == ev.Id));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ---------- Người dùng và loại vé ----------

    [Fact]
    public async Task Handle_UserNotSignedIn_ReturnsUnauthorizedAndDoesNotSave()
    {
        _currentUser.UserId.Returns((string?)null);
        var ev = GivenEvent();
        var vip = GivenTicket(ev.Id);
        GivenTicketsInStore(vip);

        var result = await Handler().Handle(CommandFor((vip, 1)), CancellationToken.None);

        Assert.Equal("Orders.Unauthorized", result.Error.Code);
        _orders.DidNotReceive().Add(Arg.Any<Order>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TicketTypeNotFound_ReturnsNotFoundAndDoesNotSave()
    {
        var ev = GivenEvent();
        var vip = GivenTicket(ev.Id);
        GivenTicketsInStore(); // kho không có loại vé nào

        var result = await Handler().Handle(CommandFor((vip, 1)), CancellationToken.None);

        Assert.Equal("TicketTypes.NotFound", result.Error.Code);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ---------- Luật đơn hàng (từ Order.Create) ----------

    [Fact]
    public async Task Handle_TicketsFromTwoEvents_ReturnsMixedEventsAndDoesNotSave()
    {
        var evA = GivenEvent();
        var evB = GivenEvent();
        var vipA = GivenTicket(evA.Id);
        var vipB = GivenTicket(evB.Id);
        GivenTicketsInStore(vipA, vipB);

        var result = await Handler().Handle(CommandFor((vipA, 1), (vipB, 1)), CancellationToken.None);

        Assert.Equal(OrderErrors.MixedEvents, result.Error);
        Assert.Equal(10, vipA.AvailableQuantity);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_QuantityOverMaxPerOrder_ReturnsErrorAndDoesNotSave()
    {
        var ev = GivenEvent();
        var vip = GivenTicket(ev.Id, maxPerOrder: 4);
        GivenTicketsInStore(vip);

        var result = await Handler().Handle(CommandFor((vip, 5)), CancellationToken.None);

        Assert.Equal(OrderErrors.ExceedsMaxPerOrder, result.Error);
        Assert.Equal(10, vip.AvailableQuantity);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ---------- Sự kiện đang mở bán ----------

    [Fact]
    public async Task Handle_EventNotFound_ReturnsNotFoundAndDoesNotSave()
    {
        var ev = GivenEvent();
        _events.GetByIdAsync(ev.Id, Arg.Any<CancellationToken>()).Returns((Event?)null);
        var vip = GivenTicket(ev.Id);
        GivenTicketsInStore(vip);

        var result = await Handler().Handle(CommandFor((vip, 1)), CancellationToken.None);

        Assert.Equal(EventErrors.NotFound(ev.Id), result.Error);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EventNotPublished_ReturnsNotOnSaleAndDoesNotSave()
    {
        var ev = GivenEvent(publish: false);
        var vip = GivenTicket(ev.Id);
        GivenTicketsInStore(vip);

        var result = await Handler().Handle(CommandFor((vip, 1)), CancellationToken.None);

        Assert.Equal(OrderErrors.NotOnSale, result.Error);
        Assert.Equal(10, vip.AvailableQuantity);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_BeforeSaleStarts_ReturnsNotOnSale()
    {
        var ev = GivenEvent(saleStartsAt: _now.AddHours(1), saleEndsAt: _now.AddDays(10));
        var vip = GivenTicket(ev.Id);
        GivenTicketsInStore(vip);

        var result = await Handler().Handle(CommandFor((vip, 1)), CancellationToken.None);

        Assert.Equal(OrderErrors.NotOnSale, result.Error);
    }

    [Fact]
    public async Task Handle_AfterSaleEnds_ReturnsNotOnSale()
    {
        var ev = GivenEvent(saleStartsAt: _now.AddDays(-10), saleEndsAt: _now.AddHours(-1));
        var vip = GivenTicket(ev.Id);
        GivenTicketsInStore(vip);

        var result = await Handler().Handle(CommandFor((vip, 1)), CancellationToken.None);

        Assert.Equal(OrderErrors.NotOnSale, result.Error);
    }

    // ---------- Trừ vé ----------

    [Fact]
    public async Task Handle_NotEnoughTickets_ReturnsSoldOutAndDoesNotSave()
    {
        var ev = GivenEvent();
        var vip = GivenTicket(ev.Id, total: 2, maxPerOrder: 2);
        vip.Reserve(1); // còn 1 vé, nhưng đơn xin 2
        GivenTicketsInStore(vip);

        var result = await Handler().Handle(CommandFor((vip, 2)), CancellationToken.None);

        Assert.Equal(TicketTypeErrors.SoldOut, result.Error);
        Assert.Equal(1, vip.AvailableQuantity);
        _orders.DidNotReceive().Add(Arg.Any<Order>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SecondLineSoldOut_DoesNotSaveFirstLine()
    {
        // Dòng VIP đủ vé, dòng Thường hết vé: không được lưu bất kỳ dòng nào.
        var ev = GivenEvent();
        var vip = GivenTicket(ev.Id, "VIP", total: 10, maxPerOrder: 4);
        var normal = GivenTicket(ev.Id, "Thường", total: 2, maxPerOrder: 2);
        normal.Reserve(2); // hết vé
        GivenTicketsInStore(vip, normal);

        var result = await Handler().Handle(CommandFor((vip, 2), (normal, 1)), CancellationToken.None);

        Assert.Equal(TicketTypeErrors.SoldOut, result.Error);
        _orders.DidNotReceive().Add(Arg.Any<Order>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
