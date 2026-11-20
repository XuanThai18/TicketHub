using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.UnitTests.Domain;

public sealed class TicketTypeStockTests
{
    // Loại vé có 10 vé, mỗi đơn tối đa 4. Mọi vé còn trống lúc tạo.
    private static TicketType GivenTicketType() =>
        TicketType.Create(Guid.NewGuid(), "VIP", null, 500_000m, totalQuantity: 10, maxQuantityPerOrder: 4).Value;

    // ---------- Reserve ----------

    [Fact]
    public void Reserve_ValidQuantity_DecreasesAvailable()
    {
        var ticket = GivenTicketType();

        var result = ticket.Reserve(3);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, ticket.AvailableQuantity);
    }

    [Fact]
    public void Reserve_ExactlyAvailable_LeavesZero()
    {
        // Ranh giới: đúng bằng số còn trống vẫn hợp lệ.
        var ticket = GivenTicketType();

        var result = ticket.Reserve(10);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, ticket.AvailableQuantity);
    }

    [Fact]
    public void Reserve_MoreThanAvailable_ReturnsSoldOutAndKeepsAvailable()
    {
        var ticket = GivenTicketType();

        var result = ticket.Reserve(11);

        Assert.Equal(TicketTypeErrors.SoldOut, result.Error);
        Assert.Equal(10, ticket.AvailableQuantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Reserve_NonPositiveQuantity_ReturnsInvalidReserveQuantity(int quantity)
    {
        var ticket = GivenTicketType();

        var result = ticket.Reserve(quantity);

        Assert.Equal(TicketTypeErrors.InvalidReserveQuantity, result.Error);
        Assert.Equal(10, ticket.AvailableQuantity);
    }
}
