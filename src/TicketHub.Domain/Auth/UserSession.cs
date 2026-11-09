namespace TicketHub.Domain.Auth;

/// <summary>
/// Một phiên đăng nhập trên một thiết bị = một refresh token còn hiệu lực.
/// Mỗi lần refresh sinh phiên mới cùng FamilyId và vô hiệu phiên cũ (rotation).
/// </summary>
public sealed class UserSession
{
    public const int UserAgentMaxLength = 512;
    public const int IpMaxLength = 64;

    private UserSession()
    {
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>Mọi phiên sinh ra từ cùng một lần đăng nhập. Phát hiện token cũ bị dùng lại → thu hồi cả họ.</summary>
    public Guid FamilyId { get; private set; }

    /// <summary>SHA-256 của refresh token. Không lưu token gốc: lộ DB cũng không dùng được.</summary>
    public string RefreshTokenHash { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public SessionRevokeReason? RevokedReason { get; private set; }
    public Guid? ReplacedBySessionId { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }

    public static UserSession Start(
        Guid userId, string refreshTokenHash, DateTimeOffset now, TimeSpan lifetime, string? ipAddress, string? userAgent) =>
        New(userId, Guid.CreateVersion7(), refreshTokenHash, now, lifetime, ipAddress, userAgent);

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    /// <summary>Thay bằng phiên mới (token mới) trong cùng họ; phiên hiện tại hết hiệu lực ngay.</summary>
    public UserSession Rotate(string newRefreshTokenHash, DateTimeOffset now, TimeSpan lifetime, string? ipAddress, string? userAgent)
    {
        var next = New(UserId, FamilyId, newRefreshTokenHash, now, lifetime, ipAddress, userAgent);
        RevokedAt = now;
        RevokedReason = SessionRevokeReason.Rotated;
        ReplacedBySessionId = next.Id;
        return next;
    }

    public void Revoke(DateTimeOffset now, SessionRevokeReason reason)
    {
        if (RevokedAt is not null)
            return;

        RevokedAt = now;
        RevokedReason = reason;
    }

    /// <summary>Token đã bị thay thế nhưng vẫn được gửi lên — dấu hiệu token bị đánh cắp.</summary>
    public bool IsReusedAfterRotation(DateTimeOffset now, TimeSpan graceWindow) =>
        RevokedReason == SessionRevokeReason.Rotated && RevokedAt is { } revokedAt && now - revokedAt > graceWindow;

    private static UserSession New(
        Guid userId, Guid familyId, string refreshTokenHash, DateTimeOffset now, TimeSpan lifetime, string? ipAddress, string? userAgent) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            FamilyId = familyId,
            RefreshTokenHash = refreshTokenHash,
            CreatedAt = now,
            ExpiresAt = now.Add(lifetime),
            IpAddress = Truncate(ipAddress, IpMaxLength),
            UserAgent = Truncate(userAgent, UserAgentMaxLength)
        };

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}

public enum SessionRevokeReason
{
    Rotated = 1,
    Logout = 2,
    LogoutAll = 3,
    PasswordChanged = 4,
    ReuseDetected = 5,
    UserDeactivated = 6
}
