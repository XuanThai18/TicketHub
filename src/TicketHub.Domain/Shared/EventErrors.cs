namespace TicketHub.Domain.Shared;

public static class EventErrors
{
    public static readonly Error InvalidSchedule = Error.Validation(
        "Events.InvalidSchedule",
        "Sale must start before it ends, end no later than the event starts, and the event must start before it ends.");

    public static readonly Error NotEditable = Error.Conflict(
        "Events.NotEditable", "Only draft events can be edited.");

    public static readonly Error NoTicketTypes = Error.Validation(
        "Events.NoTicketTypes", "An event needs at least one ticket type before it can be published.");

    public static readonly Error AlreadyCancelled = Error.Conflict(
        "Events.AlreadyCancelled", "The event is already cancelled.");

    public static Error NotFound(Guid id) => Error.NotFound("Events.NotFound", $"Event '{id}' was not found.");
}
