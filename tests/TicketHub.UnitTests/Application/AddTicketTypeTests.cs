using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Common.Exceptions;
using TicketHub.Application.TicketTypes;
using TicketHub.Domain.Events;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.UnitTests.Application;

public sealed class AddTicketTypeTests
{
    private static readonly DateTimeOffset _saleStart = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IEventRepository _events = Substitute.For<IEventRepository>();
    private readonly ITicketTypeRepository _ticketTypes = Substitute.For<ITicketTypeRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private AddTicketTypeCommandHandler Handler() => new(_events, _ticketTypes, _unitOfWork);

    // Dặn bản giả: DB có sự kiện nháp này. Bản giả NameExistsAsync mặc định trả false (tên chưa có).
    private Event GivenDraftEvent()
    {
        var existing = Event.Create("Concert", null, "Nhà hát",
            startsAt: _saleStart.AddDays(11), endsAt: _saleStart.AddDays(11).AddHours(3),
            saleStartsAt: _saleStart, saleEndsAt: _saleStart.AddDays(10)).Value;
        _events.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);
        return existing;
    }

    private static AddTicketTypeCommand CommandFor(Guid eventId) => new(
        EventId: eventId,
        Name: "VIP",
        Description: null,
        Price: 500_000m,
        TotalQuantity: 100,
        MaxQuantityPerOrder: 4);

    [Fact]
    public async Task Handle_ValidCommand_AddsTicketTypeAndSaves()
    {
        // Arrange
        var existing = GivenDraftEvent();

        // Act
        var result = await Handler().Handle(CommandFor(existing.Id), CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess);
        _ticketTypes.Received(1).Add(Arg.Is<TicketType>(t =>
            t.Id == result.Value && t.EventId == existing.Id && t.AvailableQuantity == 100));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

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
        _ticketTypes.DidNotReceive().Add(Arg.Any<TicketType>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PublishedEvent_ReturnsNotEditableAndDoesNotSave()
    {
        // Arrange
        var existing = GivenDraftEvent();
        existing.Publish(ticketTypeCount: 1);

        // Act
        var result = await Handler().Handle(CommandFor(existing.Id), CancellationToken.None);

        // Assert
        Assert.Equal(EventErrors.NotEditable, result.Error);
        _ticketTypes.DidNotReceive().Add(Arg.Any<TicketType>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DuplicateName_ReturnsConflictAndDoesNotSave()
    {
        // Arrange — DB đã có "VIP"; client gửi " VIP " (có khoảng trắng) để kiểm luôn việc Trim trước khi so.
        var existing = GivenDraftEvent();
        _ticketTypes.NameExistsAsync(existing.Id, "VIP", Arg.Any<CancellationToken>()).Returns(true);

        // Act
        var result = await Handler().Handle(CommandFor(existing.Id) with { Name = " VIP " }, CancellationToken.None);

        // Assert
        Assert.Equal(TicketTypeErrors.DuplicateName("VIP"), result.Error);
        _ticketTypes.DidNotReceive().Add(Arg.Any<TicketType>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DuplicateNameDetectedOnSave_ReturnsConflict()
    {
        // Arrange — bước kiểm trước thấy "chưa có", nhưng request khác chen vào: DB chặn bằng unique index lúc lưu.
        var existing = GivenDraftEvent();
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new UniqueConstraintException());

        // Act
        var result = await Handler().Handle(CommandFor(existing.Id), CancellationToken.None);

        // Assert
        Assert.Equal(TicketTypeErrors.DuplicateName("VIP"), result.Error);
    }

    [Fact]
    public async Task Handle_InvalidQuantities_ReturnsEntityErrorAndDoesNotSave()
    {
        // Arrange — mỗi đơn mua tối đa 200 vé nhưng chỉ phát hành 100: luật nằm trong TicketType.Create.
        var existing = GivenDraftEvent();

        // Act
        var result = await Handler().Handle(CommandFor(existing.Id) with { MaxQuantityPerOrder = 200 }, CancellationToken.None);

        // Assert
        Assert.Equal(TicketTypeErrors.InvalidMaxQuantityPerOrder, result.Error);
        _ticketTypes.DidNotReceive().Add(Arg.Any<TicketType>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
