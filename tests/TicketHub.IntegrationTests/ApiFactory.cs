using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using TicketHub.Infrastructure.Security;

[assembly: AssemblyFixture(typeof(TicketHub.IntegrationTests.ApiFactory))]

namespace TicketHub.IntegrationTests;

/// <summary>
/// Dựng API thật trên Postgres + Redis chạy trong container (cần Docker).
/// Một instance cho cả assembly: container khởi động một lần, mỗi test tự sinh dữ liệu riêng (SKU ngẫu nhiên).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        // Khởi động host MỘT lần ở đây. Nếu để test song song cùng gọi CreateClient() lần đầu,
        // Program chạy hai lần đồng thời và bootstrap logger tĩnh của Serilog bị freeze hai lần.
        _ = Server;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    public HttpClient CreateAuthenticatedClient(params string[] roles)
    {
        var client = CreateClient();
        var token = Services.GetRequiredService<JwtTokenIssuer>().Issue("test-user", roles, TimeSpan.FromMinutes(10));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Môi trường riêng "Testing": không nạp appsettings.Development.json, mọi cấu hình nằm ở đây.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", _postgres.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString());
        builder.UseSetting("JwtSettings:Secret", "integration-test-secret-0123456789abcdef");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("RateLimiting:PermitLimit", "100000");
        builder.UseSetting("RateLimiting:SensitivePermitLimit", "100000");
        // 0 giây: dùng lại token đã đổi là bị coi như đánh cắp ngay → kiểm được luồng thu hồi cả họ phiên.
        builder.UseSetting("Auth:RefreshReuseGraceSeconds", "0");
    }

    /// <summary>Client HTTPS có giữ cookie: cookie refresh token gắn cờ Secure nên chỉ gửi qua https.</summary>
    public HttpClient CreateCookieClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
}
