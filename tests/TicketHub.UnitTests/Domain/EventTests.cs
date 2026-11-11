using TicketHub.Domain.Events;
using TicketHub.Domain.Shared;

namespace TicketHub.UnitTests.Domain;

public sealed class EventTests
{
    private static readonly DateTimeOffset _saleStart = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _saleEnd = _saleStart.AddDays(10);
    private static readonly DateTimeOffset _start = _saleEnd.AddDays(1);
    private static readonly DateTimeOffset _end = _start.AddHours(3);

    private static Event NewDraft() =>
        Event.Create(" Concert ", null, "Nhà hát", _start, _end, _saleStart, _saleEnd).Value;

    [Fact]
    public void Create_ValidSchedule_IsDraftAndTrimsName()
    {
        var created = NewDraft();

        Assert.Equal(EventStatus.Draft, created.Status);
        Assert.Equal("Concert", created.Name);
    }

    [Fact]
    public void Create_SaleEndsAfterEventStarts_Fails()
    {
        var result = Event.Create("Concert", null, "Nhà hát", _start, _end, _saleStart, _start.AddHours(1));

        Assert.Equal(EventErrors.InvalidSchedule, result.Error);
    }

    [Fact]
    public void Publish_WithoutTicketTypes_Fails()
    {
        var created = NewDraft();

        Assert.Equal(EventErrors.NoTicketTypes, created.Publish(ticketTypeCount: 0).Error);
        Assert.Equal(EventStatus.Draft, created.Status);
    }

    [Fact]
    public void Update_AfterPublish_IsRejected()
    {
        var created = NewDraft();
        created.Publish(ticketTypeCount: 1);

        var result = created.Update("New", null, "X", _start, _end, _saleStart, _saleEnd);

        Assert.Equal(EventErrors.NotEditable, result.Error);
    }
}
