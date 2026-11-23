using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RabbitMQ.Client;
using TicketHub.Application.Abstractions;
using TicketHub.Application.Payments;
using TicketHub.Infrastructure.Auth;
using TicketHub.Infrastructure.Email;
using TicketHub.Infrastructure.Messaging;
using TicketHub.Infrastructure.Orders;
using TicketHub.Infrastructure.Persistence;
using TicketHub.Infrastructure.Persistence.Contexts;
using TicketHub.Infrastructure.Persistence.Interceptors;

namespace TicketHub.Infrastructure;

public static class DependencyInjection
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var dbConnectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(dbConnectionString))
            throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection.");

        var redisConnectionString = configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(redisConnectionString))
            throw new InvalidOperationException("Missing ConnectionStrings:Redis.");

        var rabbitMqConnectionString = configuration.GetConnectionString("RabbitMq");
        if (string.IsNullOrWhiteSpace(rabbitMqConnectionString))
            throw new InvalidOperationException("Missing ConnectionStrings:RabbitMq.");

        services.TryAddSingleton(TimeProvider.System);

        // Một NpgsqlDataSource (một pool) dùng chung cho EF Core, Dapper và health check.
        services.AddNpgsqlDataSource(dbConnectionString);

        services.AddScoped<AuditableEntityInterceptor>();
        services.AddScoped<SoftDeleteInterceptor>();

        services.AddDbContext<ApplicationDbContext>((sp, options) => options
            .UseNpgsql(sp.GetRequiredService<Npgsql.NpgsqlDataSource>(), npgsql => npgsql.EnableRetryOnFailure())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(
                sp.GetRequiredService<AuditableEntityInterceptor>(),
                sp.GetRequiredService<SoftDeleteInterceptor>()));

        services.AddScoped<IUnitOfWork, UnitOfWork<ApplicationDbContext>>();
        services.AddScoped<IReadDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());
        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        DapperConfiguration.Configure();
        services.AddRepositories();
        services.AddAuthModule();
        services.AddHostedService<OrderExpiryService>();

        // Outbox (Ticket 5): job nền đọc outbox_messages rồi gửi lên RabbitMQ qua một kết nối dùng chung.
        services.AddSingleton(new ConnectionFactory { Uri = new Uri(rabbitMqConnectionString) });
        services.AddSingleton<IMessagePublisher, RabbitMqPublisher>();
        services.AddHostedService<OutboxProcessor>();

        // Bên nhận: đọc queue ticket-emails rồi gửi email vé qua SMTP (dev: Mailpit).
        services.AddOptions<SmtpOptions>()
            .BindConfiguration(SmtpOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddOptions<TicketEmailConsumerOptions>()
            .BindConfiguration(TicketEmailConsumerOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddHostedService<TicketEmailConsumer>();

        // Thiếu khoá webhook (hoặc khoá ngắn) thì app không khởi động, thay vì chạy rồi mới từ chối mọi webhook.
        services.AddOptions<PaymentOptions>()
            .BindConfiguration(PaymentOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnectionString;
            options.InstanceName = "TicketHub_";
        });

        services.AddHealthChecks()
            .AddNpgSql(name: "postgres", tags: [ReadyTag], timeout: TimeSpan.FromSeconds(3))
            .AddRedis(redisConnectionString, name: "redis", tags: [ReadyTag], timeout: TimeSpan.FromSeconds(3));

        return services;
    }

    // Quy ước: class XxxRepository trong Infrastructure tự đăng ký Scoped cho interface IXxxRepository nó cài đặt.
    private static void AddRepositories(this IServiceCollection services)
    {
        var repositories = typeof(DependencyInjection).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.Name.EndsWith("Repository", StringComparison.Ordinal));

        foreach (var implementation in repositories)
        {
            foreach (var contract in implementation.GetInterfaces()
                         .Where(i => i.Name == "I" + implementation.Name))
            {
                services.AddScoped(contract, implementation);
            }
        }
    }
}
