using System.ComponentModel.DataAnnotations;

namespace TicketHub.Application.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; init; } = 15;

    [Range(1, 365)]
    public int RefreshTokenDays { get; init; } = 14;

    [Range(1, 100)]
    public int MaxFailedLoginAttempts { get; init; } = 5;

    [Range(1, 1440)]
    public int LockoutMinutes { get; init; } = 15;

    /// <summary>
    /// Token vừa bị thay trong khoảng này mà được gửi lại thì chỉ từ chối, chưa coi là bị đánh cắp
    /// (hai tab cùng refresh một lúc). Quá khoảng này → thu hồi cả họ phiên.
    /// </summary>
    [Range(0, 300)]
    public int RefreshReuseGraceSeconds { get; init; } = 10;

    /// <summary>Mặc định tắt: chỉ Admin tạo tài khoản (hệ thống nội bộ).</summary>
    public bool AllowSelfRegistration { get; init; }

    public TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(AccessTokenMinutes);
    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(RefreshTokenDays);
    public TimeSpan LockoutDuration => TimeSpan.FromMinutes(LockoutMinutes);
    public TimeSpan RefreshReuseGrace => TimeSpan.FromSeconds(RefreshReuseGraceSeconds);
}
