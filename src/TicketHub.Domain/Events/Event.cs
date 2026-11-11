
using TicketHub.Domain.Common;
using TicketHub.Domain.Shared;

namespace TicketHub.Domain.Events;

public sealed class Event : Entity
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 2000;
    public const int VenueMaxLength = 300;

    private Event() { } // For EF Core

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string Venue { get; private set; } = string.Empty;
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
    public DateTimeOffset SaleStartsAt { get; private set; }
    public DateTimeOffset SaleEndsAt { get; private set; }
    public EventStatus Status { get; private set; }

    public static Result<Event> Create(
        string name,
        string? description,
        string venue,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        DateTimeOffset saleStartsAt,
        DateTimeOffset saleEndsAt)
    {
        if (!IsValidSchedule(startsAt, endsAt, saleStartsAt, saleEndsAt))
        {
            return EventErrors.InvalidSchedule;
        }

        return new Event
        {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            Description = description?.Trim(),
            Venue = venue.Trim(),
            StartsAt = startsAt,
            EndsAt = endsAt,
            SaleStartsAt = saleStartsAt,
            SaleEndsAt = saleEndsAt,
            Status = EventStatus.Draft
        };
    }

    public Result Update(
        string name,
        string? description,
        string venue,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        DateTimeOffset saleStartsAt,
        DateTimeOffset saleEndsAt)
    {
        if (Status != EventStatus.Draft)
            return EventErrors.NotEditable;

        if (!IsValidSchedule(startsAt, endsAt, saleStartsAt, saleEndsAt))
            return EventErrors.InvalidSchedule;

        Name = name.Trim();
        Description = description?.Trim();
        Venue = venue.Trim();
        StartsAt = startsAt;
        EndsAt = endsAt;
        SaleStartsAt = saleStartsAt;
        SaleEndsAt = saleEndsAt;

        return Result.Success();
    }

    // Số loại vé truyền từ ngoài vào: Event không giữ danh sách TicketType, handler đếm rồi đưa vào.
    public Result Publish(int ticketTypeCount)
    {
        if (Status != EventStatus.Draft)
            return EventErrors.NotEditable;

        if (ticketTypeCount == 0)
            return EventErrors.NoTicketTypes;

        Status = EventStatus.Published;

        return Result.Success();
    }

    public Result Cancel()
    {
        if (Status == EventStatus.Cancelled)
            return EventErrors.AlreadyCancelled;

        Status = EventStatus.Cancelled;

        return Result.Success();
    }

    private static bool IsValidSchedule(
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        DateTimeOffset saleStartsAt,
        DateTimeOffset saleEndsAt)
    => saleStartsAt < saleEndsAt && saleEndsAt <= startsAt && startsAt < endsAt;

}
