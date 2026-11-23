using System.Text.Json;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Messaging;
using TicketHub.Application.Tickets;

namespace TicketHub.Infrastructure.Messaging;

/// <summary>
/// Nghe queue ticket-emails: mỗi tin OrderPaid → soạn email vé → gửi → ack.
/// Chỉ ack SAU khi gửi xong: app sập giữa chừng thì tin chưa ack, RabbitMQ giao lại cho lần chạy sau.
/// </summary>
internal sealed class TicketEmailConsumer(
    ConnectionFactory connectionFactory,
    IServiceScopeFactory scopeFactory,
    IEmailSender emailSender,
    ILogger<TicketEmailConsumer> logger) : BackgroundService
{
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
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
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
            var message = JsonSerializer.Deserialize<OrderPaidMessage>(delivery.Body.Span, JsonSerializerOptions.Web)
                ?? throw new JsonException("Message body is empty.");

            // Consumer là singleton còn DbContext là scoped: mỗi tin một scope riêng.
            await using var scope = scopeFactory.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            var email = await sender.Send(new GetTicketEmailQuery(message.OrderId), stoppingToken);
            if (email.IsFailure)
            {
                // Không soạn được (đơn không tồn tại, người mua đã bị xoá...): thử lại cũng vô ích → chuyển sang dead-letter.
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning("Cannot build ticket email for message {MessageId}: {Error}. Sending to dead-letter.",
                        messageId, email.Error.Message);
                }

                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, stoppingToken);
                return;
            }

            await emailSender.SendAsync(email.Value, stoppingToken);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Sent ticket email for order {OrderId} (message {MessageId}).", message.OrderId, messageId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Bước 5.4 sẽ thêm thử lại cho lỗi tạm thời (máy chủ thư chập chờn); tạm thời chuyển thẳng sang dead-letter để không lặp vô hạn.
            logger.LogError(ex, "Failed to process ticket email message {MessageId}. Sending to dead-letter.", messageId);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, stoppingToken);
        }
    }
}
