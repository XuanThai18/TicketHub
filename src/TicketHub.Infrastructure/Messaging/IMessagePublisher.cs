using TicketHub.Domain.Outbox;

namespace TicketHub.Infrastructure.Messaging;

/// <summary>Gửi một tin outbox lên broker. Chỉ trả về khi broker đã xác nhận nhận tin; hỏng thì ném lỗi.</summary>
public interface IMessagePublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}
