using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using TicketHub.Infrastructure.Caching;

namespace TicketHub.UnitTests.Infrastructure;

public sealed class DistributedCacheServiceTests
{
    private static readonly TimeSpan _ttl = TimeSpan.FromMinutes(1);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeDistributedCache _cache = new();
    private int _factoryCalls;

    private DistributedCacheService CreateService(IDistributedCache? cache = null) =>
        new(cache ?? _cache, NullLogger<DistributedCacheService>.Instance);

    private Task<Sample?> LoadFromDatabase(CancellationToken _)
    {
        _factoryCalls++;
        return Task.FromResult<Sample?>(new Sample("Concert", 3));
    }

    [Fact]
    public async Task SecondCall_IsServedFromCache_WithoutCallingFactory()
    {
        var service = CreateService();

        var first = await service.GetOrCreateAsync("k", LoadFromDatabase, _ttl, Ct);
        var second = await service.GetOrCreateAsync("k", LoadFromDatabase, _ttl, Ct);

        Assert.Equal(1, _factoryCalls);
        Assert.Equal(first, second); // đọc lại từ JSON vẫn ra đúng giá trị
    }

    [Fact]
    public async Task NullResult_IsNotCached()
    {
        var service = CreateService();
        var calls = 0;
        Task<Sample?> NotFound(CancellationToken _) { calls++; return Task.FromResult<Sample?>(null); }

        await service.GetOrCreateAsync("missing", NotFound, _ttl, Ct);
        await service.GetOrCreateAsync("missing", NotFound, _ttl, Ct);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Remove_MakesNextCallReadFromFactoryAgain()
    {
        var service = CreateService();
        await service.GetOrCreateAsync("k", LoadFromDatabase, _ttl, Ct);

        await service.RemoveAsync("k", Ct);
        await service.GetOrCreateAsync("k", LoadFromDatabase, _ttl, Ct);

        Assert.Equal(2, _factoryCalls);
    }

    [Fact]
    public async Task CacheDown_StillReturnsValueFromFactory()
    {
        // Redis chết: đọc, ghi, xoá đều ném lỗi. Request vẫn phải có dữ liệu (từ DB), không được hỏng.
        var service = CreateService(new FakeDistributedCache { IsDown = true });

        var value = await service.GetOrCreateAsync("k", LoadFromDatabase, _ttl, Ct);
        await service.RemoveAsync("k", Ct);

        Assert.Equal(new Sample("Concert", 3), value);
        Assert.Equal(1, _factoryCalls);
    }

    public sealed record Sample(string Name, int Count);

    /// <summary>Cache trong bộ nhớ đủ cho test; <see cref="IsDown"/> giả lập Redis chết.</summary>
    private sealed class FakeDistributedCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _items = [];

        public bool IsDown { get; init; }

        public byte[]? Get(string key) => throw new NotSupportedException();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();
        public void Refresh(string key) => throw new NotSupportedException();
        public void Remove(string key) => throw new NotSupportedException();
        public Task RefreshAsync(string key, CancellationToken token = default) => throw new NotSupportedException();

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            ThrowIfDown();
            return Task.FromResult(_items.GetValueOrDefault(key));
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            ThrowIfDown();
            _items[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            ThrowIfDown();
            _items.Remove(key);
            return Task.CompletedTask;
        }

        private void ThrowIfDown()
        {
            if (IsDown)
            {
                throw new InvalidOperationException("Redis is down (simulated).");
            }
        }
    }
}
