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

    // Tải kèm Items để huỷ đơn còn biết cần trả bao nhiêu vé cho từng loại. Có tracking vì sẽ đổi trạng thái.
    public Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Set<Order>()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
}
