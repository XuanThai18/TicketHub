namespace TicketHub.Api.Options;

public sealed class CorsOptions
{
    public const string SectionName = "Cors";

    /// <summary>Rỗng = không cho gọi cross-origin.</summary>
    public string[] AllowedOrigins { get; init; } = [];

    /// <summary>Cho phép gửi cookie cross-origin (FE khác origin dùng refresh token qua cookie).</summary>
    public bool AllowCredentials { get; init; }
}
