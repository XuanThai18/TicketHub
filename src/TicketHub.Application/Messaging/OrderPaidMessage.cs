using System.Text.Json;
using TicketHub.Domain.Outbox;

namespace TicketHub.Application.Messaging;

/// <summary>Đơn vừa thanh toán xong và đã có vé → cần gửi email vé cho người mua.</summary>
/// <remarks>Chỉ mang Id; bên nhận tự đọc đơn, vé và email lúc xử lý nên không chép dữ liệu cá nhân vào outbox.</remarks>
public sealed record OrderPaidMessage(Guid OrderId, Guid UserId)
{
    public const string Type = "OrderPaid";

    public OutboxMessage ToOutbox(DateTimeOffset occurredAt) =>
        OutboxMessage.Create(Type, JsonSerializer.Serialize(this, JsonSerializerOptions.Web), occurredAt);
}
