using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketHub.Application.Abstractions;
using TicketHub.Infrastructure.Auth;
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
