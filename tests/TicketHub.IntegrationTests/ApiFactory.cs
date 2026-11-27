using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using TicketHub.Application.Abstractions;
using TicketHub.Infrastructure.Messaging;
using TicketHub.Infrastructure.Security;

[assembly: AssemblyFixture(typeof(TicketHub.IntegrationTests.ApiFactory))]

namespace TicketHub.IntegrationTests;

/// <summary>
/// Dựng API thật trên Postgres + Redis + RabbitMQ + Mailpit chạy trong container (cần Docker).
/// Một instance cho cả assembly: container khởi động một lần, mỗi test tự sinh dữ liệu riêng (SKU ngẫu nhiên).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Khoá ký webhook thanh toán trong test; test tự ký webhook bằng khoá này để giả làm cổng.</summary>
    public const string WebhookSecret = "integration-test-webhook-secret-0123456789";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4-alpine").Build();

    // Mailpit: hộp thư giả nhận email qua SMTP (1025), test đọc thư qua HTTP API (8025).
    private readonly IContainer _mailpit = new ContainerBuilder("axllent/mailpit:v1.34")
        .WithPortBinding(1025, assignRandomHostPort: true)
        .WithPortBinding(8025, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/livez")))
        .Build();

    /// <summary>Địa chỉ HTTP API của Mailpit, để test kiểm email đã tới.</summary>
    public Uri MailpitApi => new($"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(8025)}");

    /// <summary>Để test tự kết nối RabbitMQ đọc tin trong queue.</summary>
    public string RabbitMqConnectionString => _rabbitMq.GetConnectionString();

    /// <summary>Bật <see cref="ControllablePublisher.BrokerDown"/> để giả lập RabbitMQ chết.</summary>
    public ControllablePublisher Publisher => Services.GetRequiredService<ControllablePublisher>();

    /// <summary>Làm lỗi vài lần gửi email để kiểm luồng thử lại / dead-letter.</summary>
    public ControllableEmailSender EmailSender => Services.GetRequiredService<ControllableEmailSender>();

    /// <summary>Số lần thử lại email trong test (thật là 5, mỗi lần cách 30s; test rút còn 2 lần, cách 1s).</summary>
    public const int TicketEmailMaxRetries = 2;

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _rabbitMq.StartAsync(), _mailpit.StartAsync());

        // Khởi động host MỘT lần ở đây. Nếu để test song song cùng gọi CreateClient() lần đầu,
        // Program chạy hai lần đồng thời và bootstrap logger tĩnh của Serilog bị freeze hai lần.
        _ = Server;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
        await _rabbitMq.DisposeAsync();
        await _mailpit.DisposeAsync();
    }

    public HttpClient CreateAuthenticatedClient(params string[] roles) =>
        CreateAuthenticatedClient(Guid.NewGuid(), roles);

    // Sub của token là Id người dùng (GUID), giống user thật; handler đặt hàng cần đọc được GUID này.
    public HttpClient CreateAuthenticatedClient(Guid userId, params string[] roles)
    {
        var client = CreateClient();
        var token = Services.GetRequiredService<JwtTokenIssuer>().Issue(userId.ToString(), roles, TimeSpan.FromMinutes(10));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Môi trường riêng "Testing": không nạp appsettings.Development.json, mọi cấu hình nằm ở đây.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", _postgres.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString());
        builder.UseSetting("ConnectionStrings:RabbitMq", _rabbitMq.GetConnectionString());
        builder.UseSetting("Smtp:Host", _mailpit.Hostname);
        builder.UseSetting("Smtp:Port", _mailpit.GetMappedPublicPort(1025).ToString(CultureInfo.InvariantCulture));
        builder.UseSetting("TicketEmailConsumer:MaxRetries", TicketEmailMaxRetries.ToString(CultureInfo.InvariantCulture));
        builder.UseSetting("TicketEmailConsumer:RetryDelay", "00:00:01");
        builder.UseSetting("JwtSettings:Secret", "integration-test-secret-0123456789abcdef");
        builder.UseSetting("Payments:WebhookSecret", WebhookSecret);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("RateLimiting:PermitLimit", "100000");
        builder.UseSetting("RateLimiting:SensitivePermitLimit", "100000");
        // 0 giây: dùng lại token đã đổi là bị coi như đánh cắp ngay → kiểm được luồng thu hồi cả họ phiên.
        builder.UseSetting("Auth:RefreshReuseGraceSeconds", "0");

        // Bọc publisher và bộ gửi email thật để test giả lập được RabbitMQ chết, máy chủ thư lỗi.
        builder.ConfigureTestServices(services =>
        {
            Decorate<IMessagePublisher, ControllablePublisher>(services, inner => new ControllablePublisher(inner));
            Decorate<IEmailSender, ControllableEmailSender>(services, inner => new ControllableEmailSender(inner));
        });
    }

    // Thay đăng ký TService bằng TWrapper bọc bản thật; test lấy TWrapper ra để điều khiển.
    private static void Decorate<TService, TWrapper>(IServiceCollection services, Func<TService, TWrapper> wrap)
        where TService : class
        where TWrapper : class, TService
    {
        var original = services.Single(d => d.ServiceType == typeof(TService));
        services.Remove(original);
        services.AddSingleton(sp => wrap((TService)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)));
        services.AddSingleton<TService>(sp => sp.GetRequiredService<TWrapper>());
    }

    /// <summary>Client HTTPS có giữ cookie: cookie refresh token gắn cờ Secure nên chỉ gửi qua https.</summary>
    public HttpClient CreateCookieClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
}
