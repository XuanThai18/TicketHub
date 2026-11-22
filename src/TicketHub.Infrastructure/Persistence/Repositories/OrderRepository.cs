using Microsoft.EntityFrameworkCore;
using TicketHub.Domain.Orders;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.Infrastructure.Persistence.Repositories;

internal sealed class OrderRepository(ApplicationDbContext dbContext) : IOrderRepository
{
    public void Add(Order order) => dbContext.Set<Order>().Add(order);

    // Chỉ đổi khi đơn vẫn Pending ngay tại thời điểm UPDATE: hai luồng cùng huỷ thì chỉ một luồng nhận 1 dòng.
    public async Task<bool> TryCancelPendingAsync(Guid id, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Set<Order>()
            .Where(o => o.Id == id && o.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Cancelled), cancellationToken);
        return rows == 1;
    }

    // Webhook thanh toán và job hết hạn cùng nhắm đơn Pending: ai UPDATE trước thì thắng, bên kia nhận 0 dòng.
    // Đặt status và paid_at cùng lúc vì ck_orders_paid_at đòi (status = 'Paid') = (paid_at IS NOT NULL).
    public async Task<bool> TryMarkPaidAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Set<Order>()
            .Where(o => o.Id == id && o.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, OrderStatus.Paid)
                .SetProperty(o => o.PaidAt, (DateTimeOffset?)now), cancellationToken);
        return rows == 1;
    }

    // Cùng kiểu với huỷ: chỉ luồng đổi được Pending mới nhận 1 dòng. Điều kiện hết hạn nằm trong UPDATE nên job không huỷ nhầm đơn còn hạn.
    public async Task<bool> TryExpirePendingAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Set<Order>()
            .Where(o => o.Id == id && o.Status == OrderStatus.Pending && o.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Expired), cancellationToken);
        return rows == 1;
    }

    public async Task<IReadOnlyList<Guid>> GetExpiredPendingIdsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        await dbContext.Set<Order>()
            .AsNoTracking()
            .Where(o => o.Status == OrderStatus.Pending && o.ExpiresAt <= now)
            .OrderBy(o => o.ExpiresAt)
            .Select(o => o.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

    // Tải kèm Items để huỷ đơn còn biết cần trả bao nhiêu vé cho từng loại. Có tracking vì sẽ đổi trạng thái.
    public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Set<Order>()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    // Chỉ đọc để so nội dung và trả lại đơn cũ: không tracking, đi đúng unique index (user_id, idempotency_key).
    public Task<Order?> GetByIdempotencyKeyAsync(Guid userId, string idempotencyKey, CancellationToken cancellationToken) =>
        dbContext.Set<Order>()
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.UserId == userId && o.IdempotencyKey == idempotencyKey, cancellationToken);
}
