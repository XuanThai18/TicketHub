namespace TicketHub.Domain.OrderItems;

public sealed class OrderItem 
{
    private OrderItem() { } // For EF Core

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid TicketTypeId { get; private set; }
    public int Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }

    internal static OrderItem Create(Guid orderId, Guid ticketTypeId, int quantity, decimal unitPrice) => new()
    {
        Id = Guid.CreateVersion7(),
        OrderId = orderId,
        TicketTypeId = ticketTypeId,
        Quantity = quantity,
        UnitPrice = unitPrice
    };
}
