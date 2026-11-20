namespace TicketHub.Api.Contracts.Orders;

public sealed record PlaceOrderRequest(IReadOnlyList<PlaceOrderLineRequest> Lines);

public sealed record PlaceOrderLineRequest(Guid TicketTypeId, int Quantity);
