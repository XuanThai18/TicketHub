using System.ComponentModel.DataAnnotations;

namespace TicketHub.Infrastructure.Messaging;

public sealed class TicketEmailConsumerOptions
{
    public const string SectionName = "TicketEmailConsumer";

    /// <summary>Số lần thử lại khi lỗi tạm thời (máy chủ thư, DB) trước khi bỏ tin sang dead-letter.</summary>
    [Range(0, 20)]
    public int MaxRetries { get; init; } = 5;

    /// <summary>
    /// Thời gian chờ giữa hai lần thử, như nhau cho mọi lần. RabbitMQ chỉ cho tin hết hạn khi nó ở đầu queue:
    /// thời gian chờ khác nhau (tăng dần) thì tin chờ lâu đứng đầu sẽ giữ chân cả các tin chờ ngắn phía sau.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)]
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(30);
}
