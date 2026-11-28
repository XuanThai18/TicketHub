using Microsoft.Extensions.DependencyInjection;
using TicketHub.Application.Abstractions;

namespace TicketHub.IntegrationTests;

/// <summary>ICacheService chạy trên Redis thật (Ticket 6.1): ghi một lần, các lần sau đọc từ cache, xoá thì đọc lại.</summary>
public sealed class CacheServiceTests(ApiFactory factory)
{
    private static readonly TimeSpan _ttl = TimeSpan.FromMinutes(1);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ValueIsStoredInRedis_UntilRemoved()
    {
        var cache = factory.Services.GetRequiredService<ICacheService>();
        var key = $"test:{Guid.NewGuid():N}";
        var calls = 0;
        Task<Sample?> Load(CancellationToken _) =>
            Task.FromResult<Sample?>(new Sample(++calls, DateTimeOffset.UnixEpoch.AddDays(calls)));

        var first = await cache.GetOrCreateAsync(key, Load, _ttl, Ct);
        var second = await cache.GetOrCreateAsync(key, Load, _ttl, Ct);
        await cache.RemoveAsync(key, Ct);
        var third = await cache.GetOrCreateAsync(key, Load, _ttl, Ct);

        Assert.Equal(first, second);      // lần 2 lấy từ Redis, giữ nguyên cả DateTimeOffset
        Assert.Equal(2, third!.Version);   // sau khi xoá thì đọc lại "DB"
    }

    public sealed record Sample(int Version, DateTimeOffset At);
}
