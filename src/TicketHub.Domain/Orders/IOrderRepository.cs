namespace TicketHub.Domain.Orders;

public interface IOrderRepository
{
    void Add(Order order);
    Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Chuyển Pending → Cancelled bằng một UPDATE có điều kiện. Trả true chỉ khi chính lời gọi này đổi được.</summary>
    Task<bool> TryCancelPendingAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Chuyển Pending → Expired khi đã hết hạn giữ chỗ, bằng một UPDATE có điều kiện. Trả true chỉ khi chính lời gọi này đổi được.</summary>
    Task<bool> TryExpirePendingAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Id các đơn Pending đã hết hạn, cũ nhất trước, tối đa <paramref name="limit"/> đơn.</summary>
    Task<IReadOnlyList<Guid>> GetExpiredPendingIdsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>Đơn mà user này đã tạo với Idempotency-Key này (kèm Items, chỉ đọc), hoặc null.</summary>
    Task<Order?> GetByIdempotencyKeyAsync(Guid userId, string idempotencyKey, CancellationToken cancellationToken);
}
