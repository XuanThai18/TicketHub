namespace TicketHub.Domain.Shared;

public static class TicketTypeErrors
{
    public static readonly Error InvalidPrice = Error.Validation(
        "TicketTypes.InvalidPrice", "Price must be greater than or equal to 0.");
    public static readonly Error InvalidTotalQuantity = Error.Validation(
        "TicketTypes.InvalidTotalQuantity", "Total quantity must be greater than 0.");
    public static readonly Error InvalidMaxQuantityPerOrder = Error.Validation(
        "TicketTypes.InvalidMaxQuantityPerOrder", "Max quantity per order must be greater than 0 and less than or equal to total quantity.");
    public static readonly Error TotalBelowSold = Error.Conflict(
    "TicketTypes.TotalBelowSold", "Total quantity cannot be less than the number of tickets already sold.");
}
