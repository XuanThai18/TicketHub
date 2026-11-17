using NSubstitute;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Events;
using TicketHub.Domain.Events;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.UnitTests.Application;

public sealed class PublishEventTests
{
    private static readonly DateTimeOffset _saleStart = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IEventRepository _events = Substitute.For<IEventRepository>();
    private readonly ITicketTypeRepository _ticketTypes = Substitute.For<ITicketTypeRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private PublishEventCommandHandler Handler() => new(_events, _ticketTypes, _unitOfWork);

    // Dặn cả hai bản giả cùng lúc: DB có sự kiện này, và sự kiện có <ticketTypeCount> loại vé.
    private Event GivenDraftEvent(int ticketTypeCount)
    {
        var existing = Event.Create("Concert", null, "Nhà hát",
            startsAt: _saleStart.AddDays(11), endsAt: _saleStart.AddDays(11).AddHours(3),
            saleStartsAt: _saleStart, saleEndsAt: _saleStart.AddDays(10)).Value;
        _events.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);
        _ticketTypes.CountByEventIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(ticketTypeCount);
        return existing;
    }

    [Fact]
    public async Task Handle_WithTicketTypes_PublishesAndSaves()
    {
        // Arrange
        var existing = GivenDraftEvent(ticketTypeCount: 2);

        // Act
        var result = await Handler().Handle(new PublishEventCommand(existing.Id), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(EventStatus.Published, existing.Status);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EventNotFound_ReturnsNotFoundAndDoesNotSave()
    {
        // Arrange
        var missingId = Guid.NewGuid();
        _events.GetByIdAsync(missingId, Arg.Any<CancellationToken>()).Returns((Event?)null);

        // Act
        var result = await Handler().Handle(new PublishEventCommand(missingId), CancellationToken.None);

        // Assert
        Assert.Equal(EventErrors.NotFound(missingId), result.Error);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoTicketTypes_ReturnsErrorAndDoesNotSave()
    {
        // Arrange
        var existing = GivenDraftEvent(ticketTypeCount: 0);

        // Act
        var result = await Handler().Handle(new PublishEventCommand(existing.Id), CancellationToken.None);

        // Assert
        Assert.Equal(EventErrors.NoTicketTypes, result.Error);
        Assert.Equal(EventStatus.Draft, existing.Status);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
