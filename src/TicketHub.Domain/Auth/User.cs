using TicketHub.Domain.Common;
using TicketHub.Domain.Shared;

namespace TicketHub.Domain.Auth;

/// <summary>Tài khoản đăng nhập. Không xoá mềm: muốn chặn thì Deactivate (giữ lịch sử, audit).</summary>
public sealed class User : IAuditableEntity
{
    public const int EmailMaxLength = 256;
    public const int FullNameMaxLength = 200;

    private readonly List<UserRole> _roles = [];

    private User()
    {
    }

    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string FullName { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }

    /// <summary>Đổi mỗi khi thông tin bảo mật đổi (mật khẩu, vai trò) — dùng để vô hiệu token cũ khi cần.</summary>
    public string SecurityStamp { get; private set; } = string.Empty;

    public IReadOnlyCollection<UserRole> Roles => _roles;

    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    public static Result<User> Create(string email, string fullName, string passwordHash, IEnumerable<string> roles)
    {
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = NormalizeEmail(email),
            FullName = fullName.Trim(),
            PasswordHash = passwordHash,
            IsActive = true,
            SecurityStamp = NewStamp()
        };

        var assigned = user.SetRoles(roles);
        return assigned.IsFailure ? assigned.Error : user;
    }

    /// <summary>Email lưu dạng chữ thường để so khớp và index unique không phân biệt hoa thường.</summary>
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public bool IsLockedOut(DateTimeOffset now) => LockedUntil is { } until && until > now;

    /// <summary>Ghi nhận một lần sai mật khẩu; đủ <paramref name="maxAttempts"/> lần thì khoá tạm.</summary>
    public void RegisterFailedLogin(DateTimeOffset now, int maxAttempts, TimeSpan lockoutDuration)
    {
        // Hết hạn khoá thì đếm lại từ đầu, không cộng dồn với đợt trước.
        if (LockedUntil is { } until && until <= now)
        {
            LockedUntil = null;
            FailedLoginCount = 0;
        }

        FailedLoginCount++;
        if (FailedLoginCount >= maxAttempts)
            LockedUntil = now.Add(lockoutDuration);
    }

    public void RegisterSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginCount = 0;
        LockedUntil = null;
        LastLoginAt = now;
    }

    public void ChangePassword(string newPasswordHash)
    {
        PasswordHash = newPasswordHash;
        SecurityStamp = NewStamp();
    }

    /// <summary>Thuật toán băm được nâng cấp: thay hash cũ bằng hash mới của cùng mật khẩu, không đổi stamp.</summary>
    public void UpgradePasswordHash(string newPasswordHash) => PasswordHash = newPasswordHash;

    public Result SetRoles(IEnumerable<string> roles)
    {
        var distinct = roles.Distinct(StringComparer.Ordinal).ToList();
        var unknown = distinct.FirstOrDefault(r => !Common.Roles.All.Contains(r));
        if (unknown is not null)
            return AuthErrors.UnknownRole(unknown);

        _roles.Clear();
        _roles.AddRange(distinct.Select(role => new UserRole(Id, role)));
        SecurityStamp = NewStamp();
        return Result.Success();
    }

    public void Deactivate()
    {
        IsActive = false;
        SecurityStamp = NewStamp();
    }

    public void Activate() => IsActive = true;

    private static string NewStamp() => Guid.NewGuid().ToString("N");
}

public sealed class UserRole
{
    public UserRole(Guid userId, string role)
    {
        UserId = userId;
        Role = role;
    }

    public Guid UserId { get; private set; }
    public string Role { get; private set; }
}
