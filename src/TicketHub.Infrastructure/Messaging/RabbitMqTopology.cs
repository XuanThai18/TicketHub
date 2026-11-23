using RabbitMQ.Client;
using TicketHub.Application.Messaging;

namespace TicketHub.Infrastructure.Messaging;

/// <summary>
/// Tên exchange/queue và cách nối chúng, khai báo ở MỘT chỗ cho cả bên gửi lẫn bên nhận.
/// <code>
/// tickethub.events (direct) --"OrderPaid"--> ticket-emails --(hỏng quá số lần)--> tickethub.dead-letter --> ticket-emails.dead-letter
///                                               ↑    │
///                                 (hết hạn chờ) │    │ (lỗi tạm thời)
///                                               └── ticket-emails.retry
/// </code>
/// </summary>
public static class RabbitMqTopology
{
    public const string Exchange = "tickethub.events";
    public const string TicketEmailsQueue = "ticket-emails";
    public const string TicketEmailsRetryQueue = "ticket-emails.retry";
    public const string DeadLetterExchange = "tickethub.dead-letter";
    public const string TicketEmailsDeadLetterQueue = "ticket-emails.dead-letter";

    /// <summary>
    /// Khai báo là idempotent: đã có thì RabbitMQ bỏ qua, nên bên nào kết nối trước cũng gọi được.
    /// Phải khai báo queue TRƯỚC khi gửi: gửi vào exchange chưa nối queue nào thì tin bị vứt.
    /// </summary>
    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        // durable: queue/exchange còn nguyên sau khi RabbitMQ khởi động lại.
        await channel.ExchangeDeclareAsync(Exchange, ExchangeType.Direct, durable: true, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(DeadLetterExchange, ExchangeType.Direct, durable: true, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(TicketEmailsDeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(TicketEmailsDeadLetterQueue, DeadLetterExchange, TicketEmailsQueue,
            cancellationToken: cancellationToken);

        // Queue "phòng chờ" thử lại: không ai đọc. Tin nằm đây tới hết hạn (consumer đặt Expiration) thì RabbitMQ
        // dead-letter nó qua exchange mặc định ("") với routing key = tên queue, tức là về thẳng ticket-emails.
        var retryArguments = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = string.Empty,
            ["x-dead-letter-routing-key"] = TicketEmailsQueue
        };
        await channel.QueueDeclareAsync(TicketEmailsRetryQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: retryArguments, cancellationToken: cancellationToken);

        // Tin bị consumer từ chối (nack, không requeue) sẽ chuyển sang dead-letter thay vì mất.
        // Tham số của queue không sửa được sau khi tạo, nên khai báo ngay từ đầu.
        var arguments = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = DeadLetterExchange,
            ["x-dead-letter-routing-key"] = TicketEmailsQueue
        };
        await channel.QueueDeclareAsync(TicketEmailsQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: arguments, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(TicketEmailsQueue, Exchange, routingKey: OrderPaidMessage.Type, cancellationToken: cancellationToken);
    }
}
