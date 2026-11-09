using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketHub.Application.Auth;
using TicketHub.Domain.Auth;
using TicketHub.Domain.Common;

namespace TicketHub.Infrastructure.Auth;

public sealed class AdminSeedOptions
{
    public const string SectionName = "Auth:SeedAdmin";

    public string? Email { get; init; }
    public string? Password { get; init; }
    public string FullName { get; init; } = "Administrator";
}

/// <summary>
/// Tạo tài khoản Admin đầu tiên khi DB chưa có user nào (không thì không ai đăng nhập để tạo user khác).
/// Chỉ chạy khi cấu hình Auth:SeedAdmin:Email + Password; đổi mật khẩu ngay sau lần đăng nhập đầu.
/// </summary>
internal sealed partial class AdminSeeder(
    IServiceScopeFactory scopeFactory, IOptions<AdminSeedOptions> options, ILogger<AdminSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seed = options.Value;
        if (string.IsNullOrWhiteSpace(seed.Email) || string.IsNullOrWhiteSpace(seed.Password))
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        if (await users.AnyAsync(cancellationToken))
            return;

        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var created = User.Create(seed.Email, seed.FullName, hasher.Hash(seed.Password), [Roles.Admin]);
        if (created.IsFailure)
            throw new InvalidOperationException($"Cannot seed admin: {created.Error.Message}");

        users.Add(created.Value);
        await scope.ServiceProvider.GetRequiredService<IAuthUnitOfWork>().SaveChangesAsync(cancellationToken);
        LogSeeded(created.Value.Email);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Seeded initial admin account {Email}. Change its password after first login.")]
    private partial void LogSeeded(string email);
}
