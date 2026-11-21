using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TicketHub.Application.Orders;

namespace TicketHub.Infrastructure.Orders;

/// <summary>Cứ 30 giây quét và huỷ các đơn Pending đã hết hạn giữ chỗ, trả vé về kho.</summary>
internal sealed class OrderExpiryService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<OrderExpiryService> logger) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(30);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var sender = scope.ServiceProvider.GetRequiredService<ISender>();
                    var result = await sender.Send(new ExpireDueOrdersCommand(BatchSize), stoppingToken);
                    if (result.IsSuccess && result.Value > 0)
                    {
                        if (logger.IsEnabled(LogLevel.Information))
                        {
                            logger.LogInformation("Expired {Count} pending order(s) and returned their tickets.", result.Value);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Lỗi DB tạm thời không được làm sập host (BackgroundService mặc định dừng cả app khi ném lỗi).
                    logger.LogError(ex, "Order expiry sweep failed; will retry on the next tick.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // App đang tắt: stoppingToken bị huỷ khi đang chờ lượt quét hoặc đang quét dở. Dừng êm, không phải lỗi.
        }
    }
}
