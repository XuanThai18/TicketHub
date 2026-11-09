using System.Buffers.Text;
using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TicketHub.Application.Auth;
using TicketHub.Domain.Auth;
using TicketHub.Infrastructure.Persistence;
using TicketHub.Infrastructure.Security;
using IPasswordHasher = TicketHub.Application.Auth.IPasswordHasher;

namespace TicketHub.Infrastructure.Auth;

/// <summary>PBKDF2 (HMAC-SHA512, 100k vòng, salt ngẫu nhiên) của ASP.NET Core Identity — không tự viết thuật toán băm.</summary>
internal sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private static readonly PasswordHasher<User> Inner = new();
    private static readonly Lazy<string> DummyHash = new(() => Inner.HashPassword(null!, Guid.NewGuid().ToString()));

    public string Hash(string password) => Inner.HashPassword(null!, password);

    public PasswordCheck Verify(string passwordHash, string password) =>
        Inner.VerifyHashedPassword(null!, passwordHash, password) switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
            _ => PasswordCheck.Failed
        };

    public void SimulateVerification(string password) => Inner.VerifyHashedPassword(null!, DummyHash.Value, password);
}

internal sealed class TokenService(JwtTokenIssuer issuer, IOptions<AuthOptions> options) : ITokenService
{
    public const string NameClaim = "name";

    public AccessToken CreateAccessToken(User user, Guid sessionId)
    {
        var (token, expiresAt) = issuer.Issue(
            user.Id.ToString(),
            user.Roles.Select(r => r.Role),
            options.Value.AccessTokenLifetime,
            [
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(NameClaim, user.FullName),
                new Claim(JwtRegisteredClaimNames.Sid, sessionId.ToString())
            ]);

        return new AccessToken(token, expiresAt);
    }

    // 256 bit ngẫu nhiên từ CSPRNG → không đoán được; base64url để đặt an toàn vào cookie/JSON.
    public string GenerateRefreshToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public string HashRefreshToken(string refreshToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
}

/// <summary>Unit of work riêng cho AuthDbContext, cùng hành vi (transaction, retry) với IUnitOfWork chính.</summary>
internal sealed class AuthUnitOfWork(UnitOfWork<AuthDbContext> inner) : IAuthUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        inner.SaveChangesAsync(cancellationToken);

    public Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<CancellationToken, Task<TResult>> work,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default) =>
        inner.ExecuteInTransactionAsync(work, isolationLevel, cancellationToken);

    public Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> work,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken cancellationToken = default) =>
        inner.ExecuteInTransactionAsync(work, isolationLevel, cancellationToken);
}
