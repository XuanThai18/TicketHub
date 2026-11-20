using TicketHub.Domain.Orders;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.UnitTests.Domain;

public sealed class OrderLifecycleTests
{
    private static readonly DateTimeOffset _now = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _expiresAt = _now.AddMinutes(Order.ReservationMinutes);

    // Tạo đơn Pending thật qua Order.Create, hết hạn lúc _expiresAt.
    private static Order GivenPendingOrder()
    {
        var eventId = Guid.NewGuid();
        var vip = TicketType.Create(eventId, "VIP", null, 500_000m, totalQuantity: 100, maxQuantityPerOrder: 4).Value;
        return Order.Create(Guid.NewGuid(), [(vip, 1)], _now).Value;
    }

    // ---------- Expire ----------

    [Fact]
    public void Expire_BeforeExpiresAt_ReturnsNotExpirableAndKeepsPending()
    {
        var order = GivenPendingOrder();

        var result = order.Expire(_expiresAt.AddSeconds(-1));

        Assert.Equal(OrderErrors.NotExpirable, result.Error);
        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    [Fact]
    public void Expire_ExactlyAtExpiresAt_BecomesExpired()
    {
        // Ranh giới: đúng mốc hết hạn là đã hết hạn (điều kiện là now < ExpiresAt).
        var order = GivenPendingOrder();

        var result = order.Expire(_expiresAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Expired, order.Status);
    }

    [Fact]
    public void Expire_AfterExpiresAt_BecomesExpired()
    {
        var order = GivenPendingOrder();

        var result = order.Expire(_expiresAt.AddMinutes(5));

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Expired, order.Status);
    }

    [Fact]
    public void Expire_AlreadyExpired_ReturnsNotExpirable()
    {
        // Job quét hai lần không được trả vé hai lần: lần hai phải bị từ chối.
        var order = GivenPendingOrder();
        order.Expire(_expiresAt);

        var result = order.Expire(_expiresAt.AddMinutes(1));

        Assert.Equal(OrderErrors.NotExpirable, result.Error);
    }
}
