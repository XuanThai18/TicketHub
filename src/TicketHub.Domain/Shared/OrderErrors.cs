namespace TicketHub.Domain.Shared;

public static class OrderErrors
{
    public static readonly Error Empty = Error.Validation(
        "Orders.Empty", "An order needs at least one ticket.");

    public static readonly Error DuplicateTicketType = Error.Validation(
        "Orders.DuplicateTicketType", "Each ticket type can appear only once in an order.");

    public static readonly Error MixedEvents = Error.Validation(
        "Orders.MixedEvents", "All tickets in an order must belong to the same event.");

    public static readonly Error InvalidQuantity = Error.Validation(
        "Orders.InvalidQuantity", "Quantity must be greater than 0.");

    public static readonly Error ExceedsMaxPerOrder = Error.Validation(
        "Orders.ExceedsMaxPerOrder", "Quantity exceeds the maximum allowed per order for this ticket type.");

    public static readonly Error NotOnSale = Error.Conflict(
        "Orders.NotOnSale", "The event is not on sale.");

    public static readonly Error NotCancellable = Error.Conflict(
        "Orders.NotCancellable", "Only pending orders can be cancelled.");

    public static readonly Error NotExpirable = Error.Conflict(
        "Orders.NotExpirable", "Only pending orders that have passed their expiry time can expire.");

    public static Error NotFound(Guid id) => Error.NotFound(
        "Orders.NotFound", $"Order '{id}' was not found.");
}
