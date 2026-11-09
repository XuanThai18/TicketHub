using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TicketHub.Application.Auth;
using TicketHub.Infrastructure.Persistence;
using TicketHub.Infrastructure.Persistence.Interceptors;

namespace TicketHub.Infrastructure.Auth;

public static class AuthModule
{
    public static IServiceCollection AddAuthModule(this IServiceCollection services)
    {
        services.AddOptions<AuthOptions>()
            .BindConfiguration(AuthOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<AdminSeedOptions>().BindConfiguration(AdminSeedOptions.SectionName);

        // Cùng pool kết nối (NpgsqlDataSource) và interceptor audit với ApplicationDbContext.
        services.AddDbContext<AuthDbContext>((sp, options) => options
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql
                .EnableRetryOnFailure()
                .MigrationsHistoryTable("__EFMigrationsHistory", AuthDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(
                sp.GetRequiredService<AuditableEntityInterceptor>(),
                sp.GetRequiredService<SoftDeleteInterceptor>()));

        services.AddScoped<UnitOfWork<AuthDbContext>>();
        services.AddScoped<IAuthUnitOfWork, AuthUnitOfWork>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<ITokenService, TokenService>();
        services.AddHostedService<AdminSeeder>();

        return services;
    }
}
