using System.Net;
using TicketHub.Infrastructure.Caching;

namespace TicketHub.UnitTests.Infrastructure;

public sealed class RedisConnectionOptionsTests
{
    [Fact]
    public void Timeouts_AreShortEnoughToFailFast()
    {
        var options = RedisConnectionOptions.Create("localhost:6379");

        Assert.True(options.ConnectTimeout <= 500);
        Assert.True(options.SyncTimeout <= 500);
    }

    [Fact]
    public void DoesNotAbortStartup_WhenRedisIsDown()
    {
        var options = RedisConnectionOptions.Create("localhost:6379");

        Assert.False(options.AbortOnConnectFail);
    }

    [Fact]
    public void KeepsTheHostAndPortFromTheConnectionString()
    {
        var options = RedisConnectionOptions.Create("redis.example:6380");

        var endPoint = Assert.IsType<DnsEndPoint>(options.EndPoints.Single());
        Assert.Equal("redis.example", endPoint.Host);
        Assert.Equal(6380, endPoint.Port);
    }
}
