using TicketHub.Domain.Outbox;

namespace TicketHub.UnitTests.Domain;

public sealed class OutboxMessageTests
{
    private static readonly DateTimeOffset _now = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_StartsUnprocessedWithNoAttempts()
    {
        var message = OutboxMessage.Create("OrderPaid", "{}", _now);

        Assert.Equal((null, 0, null), (message.ProcessedAt, message.Attempts, message.LastError));
    }

    [Fact]
    public void MarkFailed_CountsAttemptsAndStaysUnprocessed()
    {
        var message = OutboxMessage.Create("OrderPaid", "{}", _now);

        message.MarkFailed("Connection refused");
        message.MarkFailed("Connection refused again");

        Assert.Equal((null, 2, "Connection refused again"), (message.ProcessedAt, message.Attempts, message.LastError));
    }

    [Fact]
    public void MarkFailed_LongError_IsTruncatedToColumnSize()
    {
        var message = OutboxMessage.Create("OrderPaid", "{}", _now);

        message.MarkFailed(new string('x', OutboxMessage.LastErrorMaxLength + 500));

        Assert.Equal(OutboxMessage.LastErrorMaxLength, message.LastError!.Length);
    }

    [Fact]
    public void MarkProcessed_AfterFailures_SetsProcessedAtAndClearsError()
    {
        var message = OutboxMessage.Create("OrderPaid", "{}", _now);
        message.MarkFailed("Connection refused");

        message.MarkProcessed(_now.AddSeconds(5));

        Assert.Equal((_now.AddSeconds(5), 1, null), (message.ProcessedAt, message.Attempts, message.LastError));
    }
}
