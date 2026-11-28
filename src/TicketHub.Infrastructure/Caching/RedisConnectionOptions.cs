using StackExchange.Redis;

namespace TicketHub.Infrastructure.Caching;

/// <summary>Cấu hình kết nối Redis cho cache: timeout ngắn để Redis chết không làm request chờ 5 giây.</summary>
internal static class RedisConnectionOptions
{
    internal const int TimeoutMs = 300;

    internal static ConfigurationOptions Create(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.ConnectTimeout = TimeoutMs;
        options.SyncTimeout = TimeoutMs;
        // Redis chưa lên lúc khởi động thì app vẫn chạy: cache coi như miss, kết nối lại sau.
        options.AbortOnConnectFail = false;
        return options;
    }
}
