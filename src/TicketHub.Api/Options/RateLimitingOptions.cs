using System.ComponentModel.DataAnnotations;

namespace TicketHub.Api.Options;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; init; } = 100;

    [Range(1, 3600)]
    public int WindowSeconds { get; init; } = 60;

    /// <summary>
    /// Số request mỗi cửa sổ, theo IP, cho endpoint nhạy cảm (policy "sensitive": đăng nhập, refresh...).
    /// Để rộng (100) vì cả văn phòng thường ra Internet chung một IP (NAT); chống dò mật khẩu một tài khoản
    /// là việc của cơ chế khoá tài khoản sau N lần sai, không phải giới hạn theo IP.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int SensitivePermitLimit { get; init; } = 100;
}
