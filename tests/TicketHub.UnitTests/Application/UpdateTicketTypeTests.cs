using System.Data;
using NSubstitute;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Common.Exceptions;
using TicketHub.Application.TicketTypes;
using TicketHub.Domain.Events;
using TicketHub.Domain.Shared;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.UnitTests.Application;

public sealed class UpdateTicketTypeTests
{
    private static readonly DateTimeOffset _saleStart = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IEventRepository _events = Substitute.For<IEventRepository>();
    private readonly ITicketTypeRepository _ticketTypes = Substitute.For<ITicketTypeRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();

    public UpdateTicketTypeTests()
    {
        // Transaction chạy thẳng delegate; exception bên trong (xung đột lúc lưu) đi ra nguyên vẹn như bản thật.
        _unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<Result>>>(), Arg.Any<IsolationLevel>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Func<CancellationToken, Task<Result>>>(0)(CancellationToken.None));
    }

    private UpdateTicketTypeCommandHandler Handler() => new(_events, _ticketTypes, _unitOfWork);

    // Sự kiện đang bán (Published) có một loại vé VIP 10 vé.
    private (Event Event, TicketType Ticket) GivenOnSale()
    {
        var ev = Event.Create("Concert", null, "Nhà hát",
            startsAt: _saleStart.AddDays(11), endsAt: _saleStart.AddDays(11).AddHours(3),
            saleStartsAt: _saleStart, saleEndsAt: _saleStart.AddDays(10)).Value;
        Assert.True(ev.Publish(ticketTypeCount: 1).IsSuccess);
        _events.GetByIdAsync(ev.Id, Arg.Any<CancellationToken>()).Returns(ev);

        var ticket = TicketType.Create(ev.Id, "VIP", null, 500_000m, totalQuantity: 10, maxQuantityPerOrder: 4).Value;
        _ticketTypes.GetByIdAsync(ticket.Id, Arg.Any<CancellationToken>()).Returns(ticket);
        return (ev, ticket);
    }

    private static UpdateTicketTypeCommand CommandFor(Guid eventId, Guid ticketTypeId, string name = "VIP", int total = 20) => new(
        EventId: eventId,
        TicketTypeId: ticketTypeId,
        Name: name,
        Description: null,
        Price: 600_000m,
        TotalQuantity: total,
        MaxQuantityPerOrder: 4);

    private void GivenSaveConflicts(int times)
    {
        var results = Enumerable.Range(0, times)
            .Select(_ => (Func<NSubstitute.Core.CallInfo, Task<int>>)(_ => Task.FromException<int>(new ConcurrencyConflictException())))
            .Append(_ => Task.FromResult(1))
            .ToArray();
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(results[0], results[1..]);
    }

    // ---------- Thành công ----------

    [Fact]
    public async Task Handle_EventOnSale_UpdatesTicketTypeAndSaves()
    {
        var (ev, ticket) = GivenOnSale();

        var result = await Handler().Handle(CommandFor(ev.Id, ticket.Id, total: 20), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(20, ticket.TotalQuantity);
        Assert.Equal(20, ticket.AvailableQuantity); // chưa bán vé nào: còn = tổng mới
        Assert.Equal(600_000m, ticket.Price);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ---------- Xung đột với người mua (xmin) ----------

    [Fact]
    public async Task Handle_ConflictOnce_ReloadsAndSucceeds()
    {
        var (ev, ticket) = GivenOnSale();
        GivenSaveConflicts(times: 1);

        var result = await Handler().Handle(CommandFor(ev.Id, ticket.Id), CancellationToken.None);

        Assert.True(result.IsSuccess);
        // Lần hai phải đọc lại loại vé từ DB, không dùng bản đã đọc ở lần đầu.
        await _ticketTypes.Received(2).GetByIdAsync(ticket.Id, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ConflictEveryAttempt_ReturnsConcurrentUpdateAfterThreeTries()
    {
        var (ev, ticket) = GivenOnSale();
        GivenSaveConflicts(times: 10);

        var result = await Handler().Handle(CommandFor(ev.Id, ticket.Id), CancellationToken.None);

        Assert.Equal(TicketTypeErrors.ConcurrentUpdate, result.Error);
        await _unitOfWork.Received(3).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ---------- Không sửa được ----------

    [Fact]
    public async Task Handle_EventNotFound_ReturnsNotFoundAndDoesNotSave()
    {
        var missing = Guid.NewGuid();

        var result = await Handler().Handle(CommandFor(missing, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(EventErrors.NotFound(missing), result.Error);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EventCancelled_ReturnsEventCancelledAndDoesNotSave()
    {
        var (ev, ticket) = GivenOnSale();
        Assert.True(ev.Cancel().IsSuccess);

        var result = await Handler().Handle(CommandFor(ev.Id, ticket.Id), CancellationToken.None);

        Assert.Equal(TicketTypeErrors.EventCancelled, result.Error);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TicketTypeNotFound_ReturnsNotFound()
    {
        var (ev, _) = GivenOnSale();
        var missing = Guid.NewGuid();

        var result = await Handler().Handle(CommandFor(ev.Id, missing), CancellationToken.None);

        Assert.Equal(TicketTypeErrors.NotFound(missing), result.Error);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TicketTypeOfAnotherEvent_ReturnsNotFound()
    {
        // Loại vé có thật nhưng thuộc sự kiện khác: không được sửa qua URL của sự kiện này.
        var (_, ticket) = GivenOnSale();
        var (otherEvent, _) = GivenOnSale();

        var result = await Handler().Handle(CommandFor(otherEvent.Id, ticket.Id), CancellationToken.None);

        Assert.Equal(TicketTypeErrors.NotFound(ticket.Id), result.Error);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NameUsedByAnotherTicketType_ReturnsDuplicateName()
    {
        var (ev, ticket) = GivenOnSale();
        _ticketTypes.NameExistsForOtherAsync(ev.Id, ticket.Id, "Thường", Arg.Any<CancellationToken>()).Returns(true);

        var result = await Handler().Handle(CommandFor(ev.Id, ticket.Id, name: " Thường "), CancellationToken.None);

        Assert.Equal(TicketTypeErrors.DuplicateName("Thường"), result.Error);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UniqueConstraintOnSave_ReturnsDuplicateName()
    {
        // Hai admin cùng đổi sang một tên: cả hai qua bước kiểm tên, unique index chặn người lưu sau.
        var (ev, ticket) = GivenOnSale();
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new UniqueConstraintException("duplicate")));

        var result = await Handler().Handle(CommandFor(ev.Id, ticket.Id, name: "Thường"), CancellationToken.None);

        Assert.Equal(TicketTypeErrors.DuplicateName("Thường"), result.Error);
    }

    [Fact]
    public async Task Handle_InvalidValues_ReturnsDomainError()
    {
        var (ev, ticket) = GivenOnSale();

        var result = await Handler().Handle(CommandFor(ev.Id, ticket.Id, total: 0), CancellationToken.None);

        Assert.Equal(TicketTypeErrors.InvalidTotalQuantity, result.Error);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
