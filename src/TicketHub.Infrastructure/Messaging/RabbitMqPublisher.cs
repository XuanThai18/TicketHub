using System.Text;
using RabbitMQ.Client;
using TicketHub.Domain.Outbox;

namespace TicketHub.Infrastructure.Messaging;

/// <summary>
/// Giữ một kết nối + một channel dùng lại cho mọi lần gửi (mở kết nối mới mỗi tin rất chậm).
/// Kết nối hỏng thì bỏ đi, lần gửi sau tự kết nối lại.
/// </summary>
internal sealed class RabbitMqPublisher(ConnectionFactory connectionFactory) : IMessagePublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1); // IChannel không an toàn khi nhiều luồng dùng cùng lúc
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var channel = await GetChannelAsync(cancellationToken);
            var properties = new BasicProperties
            {
                // Bên nhận dùng MessageId để nhận ra tin gửi lặp (outbox giao ít nhất một lần).
                MessageId = message.Id.ToString(),
                Type = message.Type,
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent, // tin được ghi xuống đĩa, RabbitMQ khởi động lại không mất
                Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds())
            };

            // Channel bật publisher confirms: lệnh này chỉ xong khi RabbitMQ báo đã nhận (ack),
            // ném lỗi nếu bị từ chối (nack) hoặc không queue nào nhận (mandatory).
            await channel.BasicPublishAsync(
                RabbitMqTopology.Exchange, routingKey: message.Type, mandatory: true,
                basicProperties: properties, body: Encoding.UTF8.GetBytes(message.Payload),
                cancellationToken: cancellationToken);
        }
        catch
        {
            await ResetAsync(); // kết nối có thể đã chết; lần sau mở lại từ đầu
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await ResetAsync();
        _connection = await connectionFactory.CreateConnectionAsync("TicketHub.OutboxPublisher", cancellationToken);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await RabbitMqTopology.DeclareAsync(_channel, cancellationToken);
        return _channel;
    }

    private async Task ResetAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
            _channel = null;
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _lock.Dispose();
    }
}
