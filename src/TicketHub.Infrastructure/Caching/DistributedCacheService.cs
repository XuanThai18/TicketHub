using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using TicketHub.Application.Abstractions;

namespace TicketHub.Infrastructure.Caching;

/// <summary>
/// Cài đặt <see cref="ICacheService"/> trên <see cref="IDistributedCache"/> (Redis), lưu giá trị dạng JSON.
/// Mọi lỗi của cache chỉ ghi log rồi coi như không có cache: đọc lỗi thì gọi DB, ghi lỗi thì bỏ qua.
/// Bộ ngắt mạch: sau một lỗi Redis, trong <see cref="BreakDuration"/> mọi thao tác bỏ qua Redis, để request không phải chờ timeout mỗi lần.
/// </summary>
internal sealed class DistributedCacheService(
    IDistributedCache cache,
    ILogger<DistributedCacheService> logger,
    TimeProvider? timeProvider = null) : ICacheService
{
    /// <summary>Thời gian bỏ qua Redis sau một lỗi. Hết thời gian thì lần gọi kế tiếp thử Redis lại.</summary>
    internal static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    // Thời điểm (UTC ticks) bộ ngắt mạch đóng lại; 0 nghĩa là chưa từng mở.
    private long _openUntilTicks;

    public async Task<T?> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T?>> factory, TimeSpan expiration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        var cached = await TryGetAsync(key, cancellationToken);
        if (cached is not null)
        {
            return JsonSerializer.Deserialize<T>(cached, JsonSerializerOptions.Web);
        }

        var value = await factory(cancellationToken);
        if (value is not null)
        {
            await TrySetAsync(key, JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web), expiration, cancellationToken);
        }

        return value;
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        if (IsOpen())
        {
            return;
        }

        try
        {
            await cache.RemoveAsync(key, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Không xoá được thì bản cũ còn sống tới hết hạn (TTL) rồi tự mất.
            logger.LogWarning(ex, "Cache remove failed for key {CacheKey}.", key);
            OpenBreaker();
        }
    }

    private async Task<byte[]?> TryGetAsync(string key, CancellationToken cancellationToken)
    {
        if (IsOpen())
        {
            return null;
        }

        try
        {
            return await cache.GetAsync(key, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cache read failed for key {CacheKey}; reading from the database.", key);
            OpenBreaker();
            return null;
        }
    }

    private async Task TrySetAsync(string key, byte[] value, TimeSpan expiration, CancellationToken cancellationToken)
    {
        if (IsOpen())
        {
            return;
        }

        try
        {
            await cache.SetAsync(key, value,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = expiration }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cache write failed for key {CacheKey}.", key);
            OpenBreaker();
        }
    }

    private bool IsOpen() => _clock.GetUtcNow().UtcTicks < Volatile.Read(ref _openUntilTicks);

    private void OpenBreaker() =>
        Volatile.Write(ref _openUntilTicks, _clock.GetUtcNow().Add(BreakDuration).UtcTicks);
}
