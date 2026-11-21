namespace TicketHub.Api.Contracts.Events;

public sealed record CreateEventRequest(
    string Name, string? Description, string Venue,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    DateTimeOffset SaleStartsAt, DateTimeOffset SaleEndsAt);

/// <summary>Không có Id: Id lấy từ đường dẫn /events/{id}.</summary>
public sealed record UpdateEventRequest(
    string Name, string? Description, string Venue,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    DateTimeOffset SaleStartsAt, DateTimeOffset SaleEndsAt);

public sealed record AddTicketTypeRequest(
    string Name, string? Description, decimal Price, int TotalQuantity, int MaxQuantityPerOrder);

/// <summary>Gửi giá trị mới cho mọi trường (vd TotalQuantity = 300 để thêm 100 vé vào 200). Id lấy từ đường dẫn.</summary>
public sealed record UpdateTicketTypeRequest(
    string Name, string? Description, decimal Price, int TotalQuantity, int MaxQuantityPerOrder);
