using NSubstitute;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Events;
using TicketHub.Domain.Events;
using TicketHub.Domain.Shared;

namespace TicketHub.UnitTests.Application;

public sealed class CancelEventTests
{
    private static readonly DateTimeOffset _saleStart = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IEventRepository _events = Substitute.For<IEventRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private CancelEventCommandHandler Handler() => new(_events, _unitOfWork);

    // Dặn bản giả: DB có sự kiện này (trạng thái Draft).
    private Event GivenEvent()
    {
        var existing = Event.Create("Concert", null, "Nhà hát",
            startsAt: _saleStart.AddDays(11), endsAt: _saleStart.AddDays(11).AddHours(3),
            saleStartsAt: _saleStart, saleEndsAt: _saleStart.AddDays(10)).Value;
        _events.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);
        return existing;
    }

    [Fact]
    public async Task Handle_ActiveEvent_CancelsAndSaves()
    {
        // Arrange
        var existing = GivenEvent();

        // Act
        var result = await Handler().Handle(new CancelEventCommand(existing.Id), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(EventStatus.Cancelled, existing.Status);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EventNotFound_ReturnsNotFoundAndDoesNotSave()
    {
        // Arrange
        var missingId = Guid.NewGuid();
        _events.GetByIdAsync(missingId, Arg.Any<CancellationToken>()).Returns((Event?)null);

        // Act
        var result = await Handler().Handle(new CancelEventCommand(missingId), CancellationToken.None);

        // Assert
        Assert.Equal(EventErrors.NotFound(missingId), result.Error);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_AlreadyCancelled_ReturnsErrorAndDoesNotSave()
    {
        // Arrange — huỷ một lần trước để đưa sự kiện về trạng thái Cancelled.
        var existing = GivenEvent();
        existing.Cancel();

        // Act
        var result = await Handler().Handle(new CancelEventCommand(existing.Id), CancellationToken.None);

        // Assert
        Assert.Equal(EventErrors.AlreadyCancelled, result.Error);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
