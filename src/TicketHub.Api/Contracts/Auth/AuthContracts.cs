namespace TicketHub.Api.Contracts.Auth;

public sealed record LoginRequest(string Email, string Password);

/// <summary>Bỏ trống khi dùng cookie: server đọc refresh token từ cookie HttpOnly.</summary>
public sealed record RefreshRequest(string? RefreshToken);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record RegisterRequest(string Email, string FullName, string Password);

public sealed record CreateUserRequest(string Email, string FullName, string Password, IReadOnlyList<string> Roles);

/// <summary>RefreshToken = null khi đã đặt vào cookie (client web không cần và không được thấy nó).</summary>
public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string? RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

public sealed record CreatedUserResponse(Guid Id);
