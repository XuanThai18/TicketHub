using NSubstitute;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Events;
using TicketHub.Domain.Events;
using TicketHub.Domain.Shared;

namespace TicketHub.UnitTests.Application;

public sealed class UpdateEventTests
{
    private static readonly DateTimeOffset _saleStart = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _saleEnd = _saleStart.AddDays(10);
    private static readonly DateTimeOffset _start = _saleStart.AddDays(11);
    private static readonly DateTimeOffset _end = _start.AddHours(3);

    // ---- Bản giả ----
    private readonly IEventRepository _events = Substitute.For<IEventRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private UpdateEventCommandHandler Handler() => new(_events, _unitOfWork);

    // ---- Hàm tạo dữ liệu mẫu ----
    private static Event DraftEvent() =>
        Event.Create("Concert", null, "Nhà hát",
            startsAt: _start, endsAt: _end, saleStartsAt: _saleStart, saleEndsAt: _saleEnd).Value;

    private static UpdateEventCommand CommandFor(Guid id) => new(
        Id: id,
        Name: "Concert mới",
        Description: null,
        Venue: "Sân vận động",
        StartsAt: _start,
        EndsAt: _end,
        SaleStartsAt: _saleStart,
        SaleEndsAt: _saleEnd);

    // ---- Lối 3: thành công ----
    [Fact]
    public async Task Handle_DraftEvent_UpdatesAndSaves()
    {
        // Arrange
        var existing = DraftEvent();
        _events.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);

        // Act
        var result = await Handler().Handle(CommandFor(existing.Id), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("Concert mới", existing.Name);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ---- Lối 1: không tìm thấy ----
    [Fact]
    public async Task Handle_EventNotFound_ReturnsNotFoundAndDoesNotSave()
    {
        // Arrange
        var missingId = Guid.NewGuid();
        _events.GetByIdAsync(missingId, Arg.Any<CancellationToken>()).Returns((Event?)null);

        // Act
        var result = await Handler().Handle(CommandFor(missingId), CancellationToken.None);

        // Assert
        Assert.Equal(EventErrors.NotFound(missingId), result.Error);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ---- Lối 2: không được sửa ----
    [Fact]
    public async Task Handle_PublishedEvent_ReturnsNotEditableAndDoesNotSave()
    {
        // Arrange
        var existing = DraftEvent();
        existing.Publish(ticketTypeCount: 1);
        _events.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);

        // Act
        var result = await Handler().Handle(CommandFor(existing.Id), CancellationToken.None);

        // Assert
        Assert.Equal(EventErrors.NotEditable, result.Error);
        Assert.Equal("Concert", existing.Name);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
