using Microsoft.EntityFrameworkCore;
using TicketHub.Domain.Payments;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.Infrastructure.Persistence.Repositories;

internal sealed class PaymentRepository(ApplicationDbContext dbContext) : IPaymentRepository
{
    public void Add(Payment payment) => dbContext.Set<Payment>().Add(payment);

    public Task<Payment?> GetPendingByOrderIdAsync(Guid orderId, CancellationToken cancellationToken) =>
        dbContext.Set<Payment>()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.OrderId == orderId && p.Status == PaymentStatus.Pending, cancellationToken);

    public Task<Payment?> GetByProviderTransactionIdAsync(string providerTransactionId, CancellationToken cancellationToken) =>
        dbContext.Set<Payment>()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ProviderTransactionId == providerTransactionId, cancellationToken);

    // Cổng gọi lặp (kể cả cùng lúc): chỉ lời gọi đổi được Pending mới nhận 1 dòng, lời gọi sau chờ khoá dòng rồi nhận 0.
    public async Task<bool> TryCompleteAsync(
        Guid paymentId, PaymentStatus result, string rawPayload, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Set<Payment>()
            .Where(p => p.Id == paymentId && p.Status == PaymentStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, result)
                .SetProperty(p => p.RawPayload, rawPayload)
                .SetProperty(p => p.CompletedAt, (DateTimeOffset?)now), cancellationToken);
        return rows == 1;
    }

    public Task MarkRefundRequiredAsync(Guid paymentId, CancellationToken cancellationToken) =>
        dbContext.Set<Payment>()
            .Where(p => p.Id == paymentId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PaymentStatus.RefundRequired), cancellationToken);
}
