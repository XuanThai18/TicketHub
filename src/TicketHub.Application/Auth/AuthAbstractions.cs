using TicketHub.Application.Abstractions;
using TicketHub.Domain.Auth;

namespace TicketHub.Application.Auth;

/// <summary>Unit of work của AuthDbContext (schema "auth"), tách khỏi IUnitOfWork của dữ liệu nghiệp vụ.</summary>
public interface IAuthUnitOfWork : IUnitOfWork;

public enum PasswordCheck
{
    Failed,
    Success,
    SuccessRehashNeeded
}

public interface IPasswordHasher
{
    string Hash(string password);
    PasswordCheck Verify(string passwordHash, string password);

    /// <summary>Chạy một lần băm giả khi không tìm thấy user, để thời gian phản hồi không lộ email có tồn tại.</summary>
    void SimulateVerification(string password);
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

public interface ITokenService
{
    AccessToken CreateAccessToken(User user, Guid sessionId);

    /// <summary>Chuỗi ngẫu nhiên an toàn (base64url), trả cho client đúng một lần.</summary>
    string GenerateRefreshToken();

    /// <summary>Băm một chiều để lưu và tra cứu; DB không bao giờ giữ token gốc.</summary>
    string HashRefreshToken(string refreshToken);
}
