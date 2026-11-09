namespace TicketHub.Api.Options;

public enum RefreshTokenTransport
{
    /// <summary>Cookie HttpOnly: JS không đọc được → lỗi XSS không lấy cắp được token. Mặc định cho web.</summary>
    Cookie,

    /// <summary>Trả trong JSON: client tự cất (mobile, tích hợp hệ thống).</summary>
    Body
}

public sealed class RefreshTokenCookieOptions
{
    public const string SectionName = "Auth:RefreshToken";

    /// <summary>Header client gửi khi đăng nhập để nhận refresh token trong body (vd app mobile).</summary>
    public const string ClientTypeHeader = "X-Client-Type";

    /// <summary>
    /// Header bắt buộc khi refresh/logout bằng cookie. Form của site lạ không gắn được header tuỳ ý
    /// (trình duyệt đòi preflight CORS) → chặn CSRF, bổ sung cho SameSite.
    /// </summary>
    public const string CsrfHeader = "X-CSRF";

    public RefreshTokenTransport DefaultTransport { get; init; } = RefreshTokenTransport.Cookie;
    public string CookieName { get; init; } = "tickethub_refresh";

    /// <summary>Chỉ gửi cookie tới các endpoint auth (đường dẫn phân biệt hoa thường).</summary>
    public string CookiePath { get; init; } = "/api/v1/auth";

    /// <summary>Strict: chỉ gửi trong cùng site (app.x.com và api.x.com vẫn là cùng site). None khi FE khác site.</summary>
    public SameSiteMode SameSite { get; init; } = SameSiteMode.Strict;
}
