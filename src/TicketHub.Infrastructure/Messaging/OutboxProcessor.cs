using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TicketHub.Application.Abstractions;
using TicketHub.Domain.Outbox;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.Infrastructure.Messaging;

/// <summary>
/// Cứ 2 giây lấy các tin outbox chưa gửi, gửi lên RabbitMQ, gửi được thì đánh dấu đã gửi.
/// RabbitMQ chết thì tin nằm yên trong DB và được gửi lại khi RabbitMQ sống lại: không mất tin nào.
/// </summary>
internal sealed class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    IMessagePublisher publisher,
    TimeProvider timeProvider,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await ProcessBatchAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Lỗi DB tạm thời không được làm sập host; lượt sau thử lại.
                    logger.LogError(ex, "Outbox processing failed; will retry on the next tick.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // App đang tắt: dừng êm.
        }
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var published = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            // FOR UPDATE khoá các dòng vừa lấy tới hết transaction; SKIP LOCKED bỏ qua dòng instance khác đang khoá.
            // Phòng khi chạy nhiều instance (scale ngang, rolling deploy): mỗi bên lấy một phần khác nhau, không gửi trùng.
            var batch = await dbContext.Set<OutboxMessage>()
                .FromSql($"""
                    SELECT * FROM outbox_messages
                    WHERE processed_at IS NULL
                    ORDER BY occurred_at
                    LIMIT {BatchSize}
                    FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(ct);

            var count = 0;
            foreach (var message in batch)
            {
                try
                {
                    await publisher.PublishAsync(message, ct);
                    message.MarkProcessed(timeProvider.GetUtcNow());
                    count++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    message.MarkFailed(ex.Message);
                    if (logger.IsEnabled(LogLevel.Warning))
                    {
                        logger.LogWarning(ex, "Publishing outbox message {MessageId} ({Type}) failed, attempt {Attempts}.",
                            message.Id, message.Type, message.Attempts);
                    }

                    // Broker hỏng thì các tin sau cũng hỏng: dừng lô này, giữ nguyên thứ tự, lượt sau thử lại.
                    break;
                }
            }

            // Lưu cả tin đã gửi (processed_at) lẫn tin hỏng (attempts, last_error) rồi commit, nhả khoá.
            await dbContext.SaveChangesAsync(ct);
            return count;
        }, cancellationToken: cancellationToken);

        if (published > 0 && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Published {Count} outbox message(s).", published);
        }
    }
}
