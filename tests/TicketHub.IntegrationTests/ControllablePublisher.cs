using TicketHub.Domain.Outbox;
using TicketHub.Infrastructure.Messaging;

namespace TicketHub.IntegrationTests;

/// <summary>
/// Bọc publisher RabbitMQ thật; bật <see cref="BrokerDown"/> để giả lập RabbitMQ chết.
/// Không tắt container thật vì bật lại thì Testcontainers thường cấp cổng mới, app không tìm thấy nữa.
/// </summary>
public sealed class ControllablePublisher(IMessagePublisher inner) : IMessagePublisher, IAsyncDisposable
{
    private volatile bool _brokerDown;

    public bool BrokerDown
    {
        get => _brokerDown;
        set => _brokerDown = value;
    }

    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken) =>
        BrokerDown
            ? throw new InvalidOperationException("RabbitMQ is down (simulated).")
            : inner.PublishAsync(message, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (inner is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }
}
