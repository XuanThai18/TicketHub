using TicketHub.Domain.Orders;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.UnitTests.Domain;

public sealed class OrderTests
{
    private static readonly DateTimeOffset _now = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid _userId = Guid.NewGuid();
    private static readonly Guid _eventA = Guid.NewGuid();
    private static readonly Guid _eventB = Guid.NewGuid();

    // Tạo loại vé thật qua TicketType.Create, không dùng bản giả.
    private static TicketType GivenTicketType(Guid eventId, string name = "VIP", decimal price = 500_000m, int maxPerOrder = 4) =>
        TicketType.Create(eventId, name, null, price, totalQuantity: 100, maxQuantityPerOrder: maxPerOrder).Value;

    [Fact]
    public void Create_ValidSingleLine_ReturnsPendingOrderWithTotalAndExpiry()
    {
        // Arrange
        var vip = GivenTicketType(_eventA, price: 500_000m);

        // Act
        var result = Order.Create(_userId, [(vip, 2)], _now);

        // Assert
        Assert.True(result.IsSuccess);
        var order = result.Value;
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(_eventA, order.EventId);
        Assert.Equal(_userId, order.UserId);
        Assert.Equal(1_000_000m, order.TotalAmount);
        Assert.Equal(_now.AddMinutes(Order.ReservationMinutes), order.ExpiresAt);
    }

    [Fact]
    public void Create_ValidSingleLine_CopiesPriceIntoOrderItem()
    {
        var vip = GivenTicketType(_eventA, price: 500_000m);

        var order = Order.Create(_userId, [(vip, 2)], _now).Value;

        var item = Assert.Single(order.Items);
        Assert.Equal(vip.Id, item.TicketTypeId);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(500_000m, item.UnitPrice);
    }

    [Fact]
    public void Create_MultipleLinesSameEvent_SumsTotalAmount()
    {
        var vip = GivenTicketType(_eventA, "VIP", price: 500_000m);
        var normal = GivenTicketType(_eventA, "Thường", price: 100_000m);

        var result = Order.Create(_userId, [(vip, 2), (normal, 3)], _now);

        Assert.Equal(1_300_000m, result.Value.TotalAmount);
        Assert.Equal(2, result.Value.Items.Count);
    }

    [Fact]
    public void Create_EmptyLines_ReturnsEmptyError()
    {
        var result = Order.Create(_userId, [], _now);

        Assert.Equal(OrderErrors.Empty, result.Error);
    }

    [Fact]
    public void Create_SameTicketTypeTwice_ReturnsDuplicateError()
    {
        var vip = GivenTicketType(_eventA);

        var result = Order.Create(_userId, [(vip, 1), (vip, 2)], _now);

        Assert.Equal(OrderErrors.DuplicateTicketType, result.Error);
    }

    [Fact]
    public void Create_TicketTypesFromDifferentEvents_ReturnsMixedEventsError()
    {
        var vipA = GivenTicketType(_eventA);
        var vipB = GivenTicketType(_eventB);

        var result = Order.Create(_userId, [(vipA, 1), (vipB, 1)], _now);

        Assert.Equal(OrderErrors.MixedEvents, result.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_NonPositiveQuantity_ReturnsInvalidQuantityError(int quantity)
    {
        var vip = GivenTicketType(_eventA);

        var result = Order.Create(_userId, [(vip, quantity)], _now);

        Assert.Equal(OrderErrors.InvalidQuantity, result.Error);
    }

    [Fact]
    public void Create_QuantityOverMaxPerOrder_ReturnsExceedsMaxError()
    {
        var vip = GivenTicketType(_eventA, maxPerOrder: 4);

        var result = Order.Create(_userId, [(vip, 5)], _now);

        Assert.Equal(OrderErrors.ExceedsMaxPerOrder, result.Error);
    }

    [Fact]
    public void Create_QuantityEqualToMaxPerOrder_Succeeds()
    {
        // Ranh giới: đúng bằng giới hạn vẫn hợp lệ.
        var vip = GivenTicketType(_eventA, maxPerOrder: 4);

        var result = Order.Create(_userId, [(vip, 4)], _now);

        Assert.True(result.IsSuccess);
    }
}
