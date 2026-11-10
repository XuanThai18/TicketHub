namespace TicketHub.UnitTests.Domain;

public sealed class EventTests
{
    private static readonly DateTimeOffset _saleStart = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _saleEnd = _saleStart.AddDays(10);
    private static readonly DateTimeOffset _start = _saleEnd.AddDays(1);
    private static readonly DateTimeOffset _end = _start.AddHours(3);

}
