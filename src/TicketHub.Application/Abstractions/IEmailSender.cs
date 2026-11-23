namespace TicketHub.Application.Abstractions;

/// <summary>Một email đã soạn xong, sẵn sàng gửi.</summary>
public sealed record EmailMessage(string ToAddress, string ToName, string Subject, string TextBody, string HtmlBody);

/// <summary>Gửi email. Hỏng (máy chủ thư không phản hồi, sai tài khoản...) thì ném lỗi.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage email, CancellationToken cancellationToken);
}
