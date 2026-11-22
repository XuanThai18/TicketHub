namespace TicketHub.Application.Payments;

/// <summary>Nội dung webhook cổng thanh toán gửi về (JSON, tên trường camelCase).</summary>
/// <param name="TransactionId">Mã giao dịch phía cổng, trùng với payments.provider_transaction_id.</param>
/// <param name="Status"><see cref="Succeeded"/> hoặc <see cref="Failed"/>.</param>
/// <param name="OccurredAt">Lúc giao dịch kết thúc phía cổng.</param>
public sealed record PaymentWebhookPayload(string TransactionId, string Status, DateTimeOffset OccurredAt)
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}
