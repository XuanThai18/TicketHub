using System.ComponentModel.DataAnnotations;

namespace TicketHub.Infrastructure.Email;

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    [Required]
    public string Host { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; init; } = 25;

    /// <summary>Mailpit không dùng TLS; máy chủ thư thật (Gmail, SES...) thì bật.</summary>
    public bool UseSsl { get; init; }

    /// <summary>Để trống = không đăng nhập (Mailpit).</summary>
    public string? UserName { get; init; }

    public string? Password { get; init; }

    [Required, EmailAddress]
    public string FromAddress { get; init; } = string.Empty;

    public string FromName { get; init; } = "TicketHub";
}
