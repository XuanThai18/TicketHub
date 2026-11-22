namespace TicketHub.Domain.Outbox;

/// <summary>
/// Một tin nhắn chờ gửi ra ngoài (RabbitMQ). Được ghi cùng transaction với thay đổi nghiệp vụ,
/// nên commit thì có cả hai, rollback thì không có gì. Job nền gửi tin đi sau.
/// </summary>
public sealed class OutboxMessage
{
    public const int TypeMaxLength = 100;
    public const int LastErrorMaxLength = 2000;

    private OutboxMessage() { } // For EF Core

    public Guid Id { get; private set; }

    /// <summary>Loại tin, vd "OrderPaid"; bên nhận dựa vào đây để biết cách đọc <see cref="Payload"/>.</summary>
    public string Type { get; private set; } = string.Empty;

    /// <summary>Nội dung tin (JSON).</summary>
    public string Payload { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Null = chưa gửi được lên RabbitMQ.</summary>
    public DateTimeOffset? ProcessedAt { get; private set; }

    public int Attempts { get; private set; }
    public string? LastError { get; private set; }

    public static OutboxMessage Create(string type, string payload, DateTimeOffset occurredAt) => new()
    {
        Id = Guid.CreateVersion7(),
        Type = type,
        Payload = payload,
        OccurredAt = occurredAt
    };
}
