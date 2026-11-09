using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TicketHub.Infrastructure.Persistence.Contexts;

/// <summary>
/// Chỉ dùng cho "dotnet ef migrations add": dựng DbContext mà không cần chạy host API
/// hay có DB thật (tạo migration không kết nối DB).
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Port=5433;Database=tickethub;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new ApplicationDbContext(options);
    }
}
