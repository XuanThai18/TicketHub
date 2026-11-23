using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using TicketHub.Application.Abstractions;

namespace TicketHub.Infrastructure.Email;

/// <summary>Gửi email qua SMTP bằng MailKit (SmtpClient có sẵn của .NET đã bị Microsoft khuyên không dùng nữa).</summary>
internal sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage email, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(email);
        var smtp = options.Value;

        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress(smtp.FromName, smtp.FromAddress));
        message.To.Add(new MailboxAddress(email.ToName, email.ToAddress));
        message.Subject = email.Subject;
        // Gửi cả bản chữ thường lẫn HTML: trình đọc thư không hiện HTML thì vẫn đọc được bản chữ.
        message.Body = new BodyBuilder { TextBody = email.TextBody, HtmlBody = email.HtmlBody }.ToMessageBody();

        // Mỗi email một kết nối: đơn giản, đủ dùng với lượng email vé. Gửi số lượng lớn thì nên giữ kết nối lại.
        using var client = new SmtpClient();
        await client.ConnectAsync(smtp.Host, smtp.Port, smtp.UseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None, cancellationToken);
        if (!string.IsNullOrEmpty(smtp.UserName))
        {
            await client.AuthenticateAsync(smtp.UserName, smtp.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}
