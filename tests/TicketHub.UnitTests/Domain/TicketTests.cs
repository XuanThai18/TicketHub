using TicketHub.Domain.Tickets;

namespace TicketHub.UnitTests.Domain;

public sealed class TicketTests
{
    private static readonly DateTimeOffset _now = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Issue_CreatesValidTicketWithReadableCode()
    {
        var orderId = Guid.NewGuid();
        var ticketTypeId = Guid.NewGuid();

        var ticket = Ticket.Issue(orderId, ticketTypeId, _now);

        Assert.Equal((orderId, ticketTypeId, TicketStatus.Valid, _now), (ticket.OrderId, ticket.TicketTypeId, ticket.Status, ticket.IssuedAt));
        Assert.Equal(Ticket.CodeLength, ticket.Code.Length);
        // Không có ký tự dễ đọc nhầm khi soát vé bằng tay.
        Assert.DoesNotContain(ticket.Code, c => "01OIL".Contains(c, StringComparison.Ordinal));
        Assert.All(ticket.Code, c => Assert.True(char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)));
    }

    [Fact]
    public void Issue_ManyTickets_CodesAreUnique()
    {
        var codes = Enumerable.Range(0, 10_000).Select(_ => Ticket.Issue(Guid.NewGuid(), Guid.NewGuid(), _now).Code).ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
    }
}
