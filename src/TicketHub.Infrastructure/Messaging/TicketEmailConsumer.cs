using System.Globalization;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Messaging;
using TicketHub.Application.Tickets;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.Infrastructure.Messaging;

/// <summary>
/// Nghe queue ticket-emails: mỗi tin OrderPaid → soạn email vé → gửi → ack.
/// Chỉ ack SAU khi gửi xong: app sập giữa chừng thì tin chưa ack, RabbitMQ giao lại cho lần chạy sau.
/// Tin giao lại mà đã gửi rồi thì bỏ qua (processed_messages); lỗi tạm thời thì hẹn giờ thử lại; hỏng hẳn thì sang dead-letter.
/// </summary>
internal sealed class TicketEmailConsumer(
    ConnectionFactory connectionFactory,
    IServiceScopeFactory scopeFactory,
    IEmailSender emailSender,
    IOptions<TicketEmailConsumerOptions> options,
    TimeProvider timeProvider,
    ILogger<TicketEmailConsumer> logger) : BackgroundService
{
    /// <summary>Tên ghi vào processed_messages.consumer.</summary>
    internal const string ConsumerName = "ticket-emails";

    /// <summary>Header đếm số lần tin đã được thử lại.</summary>
    internal const string RetryCountHeader = "x-retry-count";

    private static readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(5);
    private const ushort PrefetchCount = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Lúc app khởi động RabbitMQ có thể chưa sẵn sàng: thử kết nối lại tới khi được.
            // Kết nối rồi mà đứt giữa chừng thì RabbitMQ.Client tự nối lại và tự đăng ký lại consumer (automatic recovery).
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ConsumeAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Ticket email consumer could not connect to RabbitMQ; retrying in {Delay}.", _reconnectDelay);
                    await Task.Delay(_reconnectDelay, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // App đang tắt. Tin đang xử lý dở chưa ack sẽ được RabbitMQ giao lại sau.
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        await using var connection = await connectionFactory.CreateConnectionAsync("TicketHub.TicketEmailConsumer", stoppingToken);

        // Bật publisher confirms: khi chuyển tin sang queue thử lại, phải chắc RabbitMQ đã nhận bản sao rồi mới ack bản gốc.
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            stoppingToken);
        await RabbitMqTopology.DeclareAsync(channel, stoppingToken);

        // Mỗi lần nhận tối đa 10 tin chưa ack; tránh RabbitMQ dồn cả nghìn tin vào một consumer.
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: PrefetchCount, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, delivery, stoppingToken);

        // autoAck: false → tự ack bằng tay sau khi gửi xong email.
        await channel.BasicConsumeAsync(RabbitMqTopology.TicketEmailsQueue, autoAck: false, consumer, stoppingToken);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Ticket email consumer is listening on {Queue}.", RabbitMqTopology.TicketEmailsQueue);
        }

        // Tin tới thì ReceivedAsync tự chạy; ở đây chỉ việc giữ kết nối mở tới khi app tắt.
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        var messageId = delivery.BasicProperties.MessageId;
        try
        {
            var rejectReason = await ProcessAsync(messageId, delivery.Body, stoppingToken);
            if (rejectReason is null)
            {
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
                return;
            }

            // Tin hỏng hẳn (sai định dạng, đơn không tồn tại, người mua đã bị xoá...): thử lại cũng vô ích.
            if (logger.IsEnabled(LogLevel.Warning))
            {
                logger.LogWarning("Rejecting ticket email message {MessageId}: {Reason}. Sending to dead-letter.", messageId, rejectReason);
            }

            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Lỗi tạm thời (máy chủ thư chập chờn, DB mất kết nối): hẹn giờ thử lại.
            await RetryOrDeadLetterAsync(channel, delivery, ex, stoppingToken);
        }
    }

    /// <summary>
    /// Trả về null nếu xong việc (vừa gửi, hoặc tin lặp đã gửi từ trước); trả về lý do nếu tin hỏng hẳn.
    /// Ném exception nghĩa là lỗi tạm thời, đáng thử lại.
    /// </summary>
    private async Task<string?> ProcessAsync(string? messageIdText, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(messageIdText, out var messageId))
        {
            return "missing or invalid MessageId";
        }

        OrderPaidMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<OrderPaidMessage>(body.Span, JsonSerializerOptions.Web);
        }
        catch (JsonException ex)
        {
            return $"invalid JSON ({ex.Message})";
        }

        if (message is null)
        {
            return "empty body";
        }

        // Consumer là singleton còn DbContext là scoped: mỗi tin một scope riêng.
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var processedMessages = dbContext.Set<ProcessedMessage>();

        // Tin đã xử lý mà tới lại (app sập trước khi kịp ack, outbox gửi lặp): bỏ qua, ack để RabbitMQ xoá.
        if (await processedMessages.AnyAsync(p => p.MessageId == messageId && p.Consumer == ConsumerName, cancellationToken))
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Message {MessageId} was already processed; skipping.", messageId);
            }

            return null;
        }

        var email = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(new GetTicketEmailQuery(message.OrderId), cancellationToken);
        if (email.IsFailure)
        {
            return email.Error.Message;
        }

        await emailSender.SendAsync(email.Value, cancellationToken);

        // Ghi dấu SAU khi gửi: sập giữa hai bước thì lần sau gửi lại (thừa một thư), còn hơn ghi trước rồi mất thư.
        processedMessages.Add(ProcessedMessage.Create(messageId, ConsumerName, timeProvider.GetUtcNow()));
        await dbContext.SaveChangesAsync(cancellationToken);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Sent ticket email for order {OrderId} (message {MessageId}).", message.OrderId, messageId);
        }

        return null;
    }

    private async Task RetryOrDeadLetterAsync(IChannel channel, BasicDeliverEventArgs delivery, Exception error, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var messageId = delivery.BasicProperties.MessageId;
        var retryCount = GetRetryCount(delivery.BasicProperties);

        if (retryCount >= settings.MaxRetries)
        {
            logger.LogError(error, "Ticket email message {MessageId} failed after {RetryCount} retries. Sending to dead-letter.",
                messageId, retryCount);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, cancellationToken);
            return;
        }

        var properties = new BasicProperties
        {
            MessageId = messageId,
            Type = delivery.BasicProperties.Type,
            ContentType = delivery.BasicProperties.ContentType,
            Timestamp = delivery.BasicProperties.Timestamp,
            DeliveryMode = DeliveryModes.Persistent,
            Headers = new Dictionary<string, object?> { [RetryCountHeader] = retryCount + 1 },
            // Tin nằm trong queue thử lại đúng chừng này mili giây rồi RabbitMQ đẩy về ticket-emails.
            Expiration = ((long)settings.RetryDelay.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
        };

        try
        {
            // Gửi bản sao sang queue thử lại (exchange mặc định "", routing key = tên queue) RỒI mới ack bản gốc.
            // Sập giữa hai bước thì có hai bản; bản tới sau thấy dấu đã xử lý và bỏ qua.
            await channel.BasicPublishAsync(string.Empty, RabbitMqTopology.TicketEmailsRetryQueue, mandatory: true,
                properties, delivery.Body, cancellationToken);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
        }
        catch (Exception publishError) when (publishError is not OperationCanceledException)
        {
            // Không chuyển được sang queue thử lại: trả tin về queue chính, không để mất.
            logger.LogError(publishError, "Could not schedule a retry for message {MessageId}; requeueing it.", messageId);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, cancellationToken);
            return;
        }

        if (logger.IsEnabled(LogLevel.Warning))
        {
            logger.LogWarning(error, "Ticket email message {MessageId} failed; retry {Attempt}/{MaxRetries} in {Delay}.",
                messageId, retryCount + 1, settings.MaxRetries, settings.RetryDelay);
        }
    }

    // Tin mới từ outbox chưa có header → 0. RabbitMQ trả số nguyên AMQP về dưới dạng int hoặc long tuỳ lúc.
    private static int GetRetryCount(IReadOnlyBasicProperties properties) =>
        properties.Headers is not null && properties.Headers.TryGetValue(RetryCountHeader, out var value) && value is not null
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : 0;
}
