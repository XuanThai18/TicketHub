using Microsoft.EntityFrameworkCore;
using TicketHub.Domain.Auth;

namespace TicketHub.Infrastructure.Auth;

internal sealed class UserRepository(AuthDbContext dbContext) : IUserRepository
{
    public Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Set<User>().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<User?> GetByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        dbContext.Set<User>().FirstOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);

    public Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        dbContext.Set<User>().AnyAsync(u => u.Email == normalizedEmail, cancellationToken);

    public Task<bool> AnyAsync(CancellationToken cancellationToken) =>
        dbContext.Set<User>().AnyAsync(cancellationToken);

    public void Add(User user) => dbContext.Set<User>().Add(user);
}

internal sealed class UserSessionRepository(AuthDbContext dbContext) : IUserSessionRepository
{
    public Task<UserSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Set<UserSession>().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<UserSession?> GetByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken) =>
        dbContext.Set<UserSession>().FirstOrDefaultAsync(s => s.RefreshTokenHash == refreshTokenHash, cancellationToken);

    public async Task<IReadOnlyList<UserSession>> ListActiveByUserAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await dbContext.Set<UserSession>()
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<UserSession>> ListActiveByFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await dbContext.Set<UserSession>()
            .Where(s => s.FamilyId == familyId && s.RevokedAt == null && s.ExpiresAt > now)
            .ToListAsync(cancellationToken);

    public void Add(UserSession session) => dbContext.Set<UserSession>().Add(session);
}
