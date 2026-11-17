using NSubstitute;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Events;
using TicketHub.Domain.Events;
using TicketHub.Domain.Shared;

namespace TicketHub.UnitTests.Application;

public sealed class CreateEventTests
{
    private static readonly DateTimeOffset _saleStart = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IEventRepository _events = Substitute.For<IEventRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    private CreateEventCommandHandler Handler() => new(_events, _unitOfWork);

    private static CreateEventCommand ValidCommand() => new(
        Name: "Concert",
        Description: null,
        Venue: "Nhà hát",
        StartsAt: _saleStart.AddDays(11),
        EndsAt: _saleStart.AddDays(11).AddHours(3),
        SaleStartsAt: _saleStart,
        SaleEndsAt: _saleStart.AddDays(10));

    [Fact]
    public async Task Handle_ValidCommand_AddsEventAndSaves()
    {
        var result = await Handler().Handle(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        _events.Received(1).Add(Arg.Is<Event>(e => e.Id == result.Value));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_InvalidSchedule_ReturnsErrorAndDoesNotSave()
    {
        var command = ValidCommand() with { SaleEndsAt = _saleStart.AddDays(20) };

        var result = await Handler().Handle(command, CancellationToken.None);

        Assert.Equal(EventErrors.InvalidSchedule, result.Error);
        _events.DidNotReceive().Add(Arg.Any<Event>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Validator_EmptyName_Fails()
    {
        var result = new CreateEventCommandValidator().Validate(ValidCommand() with { Name = "" });

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateEventCommand.Name));
    }

    [Fact]
    public void Validator_MissingSaleStartsAt_Fails()
    {
        var result = new CreateEventCommandValidator().Validate(ValidCommand() with { SaleStartsAt = default });

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateEventCommand.SaleStartsAt));
    }
}
