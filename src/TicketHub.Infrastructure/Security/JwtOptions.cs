using System.ComponentModel.DataAnnotations;

namespace TicketHub.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string SectionName = "JwtSettings";

    [Required]
    public string Issuer { get; init; } = string.Empty;

    [Required]
    public string Audience { get; init; } = string.Empty;

    // HMAC-SHA256 cần khoá ≥ 256 bit.
    [Required, MinLength(32)]
    public string Secret { get; init; } = string.Empty;

    [Range(0, 300)]
    public int ClockSkewSeconds { get; init; } = 30;
}
