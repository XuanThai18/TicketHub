namespace TicketHub.Domain.Orders;

public interface IOrderRepository
{
    void Add(Order order);
    Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Chuyển Pending → Cancelled bằng một UPDATE có điều kiện. Trả true chỉ khi chính lời gọi này đổi được.</summary>
    Task<bool> TryCancelPendingAsync(Guid id, CancellationToken cancellationToken);
}
