namespace TicketHub.Domain.Payments;

public interface IPaymentRepository
{
    void Add(Payment payment);

    /// <summary>Lượt thanh toán đang Pending của đơn (chỉ đọc), hoặc null. Mỗi đơn có tối đa một.</summary>
    Task<Payment?> GetPendingByOrderIdAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>Lượt thanh toán theo mã giao dịch của cổng (chỉ đọc), hoặc null.</summary>
    Task<Payment?> GetByProviderTransactionIdAsync(string providerTransactionId, CancellationToken cancellationToken);

    /// <summary>
    /// Pending → <paramref name="result"/> bằng một UPDATE có điều kiện, lưu kèm webhook.
    /// Trả true chỉ khi chính lời gọi này đổi được; false = webhook này đã được xử lý trước đó.
    /// </summary>
    Task<bool> TryCompleteAsync(Guid paymentId, PaymentStatus result, string rawPayload, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Đánh dấu cần hoàn tiền (tiền về khi đơn không còn Pending).</summary>
    Task MarkRefundRequiredAsync(Guid paymentId, CancellationToken cancellationToken);
}
