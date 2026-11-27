namespace TicketHub.Infrastructure.Messaging;

/// <summary>
/// Dấu "consumer này đã xử lý xong tin này". RabbitMQ giao ít nhất một lần, nên cùng một tin có thể tới lại;
/// gặp MessageId đã có dấu thì consumer bỏ qua, không gửi email lần hai.
/// </summary>
internal sealed class ProcessedMessage
{
    public const int ConsumerMaxLength = 100;

    private ProcessedMessage() { }

    /// <summary>MessageId của tin RabbitMQ (= Id dòng outbox), không đổi dù tin bị giao lại bao nhiêu lần.</summary>
    public Guid MessageId { get; private set; }

    /// <summary>Tên consumer: một tin có thể tới nhiều consumer, mỗi bên tự đánh dấu phần việc của mình.</summary>
    public string Consumer { get; private set; } = string.Empty;

    public DateTimeOffset ProcessedAt { get; private set; }

    public static ProcessedMessage Create(Guid messageId, string consumer, DateTimeOffset processedAt) =>
        new() { MessageId = messageId, Consumer = consumer, ProcessedAt = processedAt };
}
