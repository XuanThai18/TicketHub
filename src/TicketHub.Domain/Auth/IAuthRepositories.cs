namespace TicketHub.Domain.Auth;

/// <summary>Phía ghi của module Auth. Phía đọc (/me, danh sách phiên) dùng Dapper trong query handler.</summary>
public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<User?> GetByEmailAsync(string normalizedEmail, CancellationToken cancellationToken);
    Task<bool> EmailExistsAsync(string normalizedEmail, CancellationToken cancellationToken);
    Task<bool> AnyAsync(CancellationToken cancellationToken);
    void Add(User user);
}

public interface IUserSessionRepository
{
    Task<UserSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<UserSession?> GetByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken);
    Task<IReadOnlyList<UserSession>> ListActiveByUserAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<UserSession>> ListActiveByFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken);
    void Add(UserSession session);
}
