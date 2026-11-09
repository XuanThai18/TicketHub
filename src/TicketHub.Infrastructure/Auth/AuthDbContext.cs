using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.Infrastructure.Auth;

/// <summary>
/// Dữ liệu tài khoản tách khỏi dữ liệu nghiệp vụ: schema "auth", migration và bảng lịch sử migration riêng.
/// Nhờ vậy module bỏ được (--auth none) mà không đụng migration của ApplicationDbContext.
/// </summary>
public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : AppDbContextBase(options)
{
    public const string Schema = "auth";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(AuthDbContext).Assembly,
            t => t.Namespace?.StartsWith(ApplicationDbContext.ModulesNamespace, StringComparison.Ordinal) == true);
        ApplySoftDeleteQueryFilter(modelBuilder);
    }
}

/// <summary>Cho "dotnet ef migrations add ... --context AuthDbContext"; không cần DB thật.</summary>
internal sealed class AuthDesignTimeDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Port=5433;Database=tickethub;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", AuthDbContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AuthDbContext(options);
    }
}
