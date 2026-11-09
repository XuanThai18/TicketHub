using Microsoft.EntityFrameworkCore;
using TicketHub.Infrastructure.Auth;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.Api.Extensions;

public static class DatabaseSetup
{
    /// <summary>
    /// Chỉ dùng cho local/dev (Database:MigrateOnStartup=true). Production chạy migration
    /// bằng bước riêng trong CI/CD: dotnet ef migrations bundle hoặc script idempotent.
    /// </summary>
    public static async Task ApplyMigrationsIfEnabledAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
            return;

        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.MigrateAsync();
    }
}
