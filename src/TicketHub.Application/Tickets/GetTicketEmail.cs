using System.Globalization;
using System.Net;
using System.Text;
using MediatR;
using Microsoft.EntityFrameworkCore;
using TicketHub.Application.Abstractions;
using TicketHub.Domain.Auth;
using TicketHub.Domain.Events;
using TicketHub.Domain.Orders;
using TicketHub.Domain.Shared;
using TicketHub.Domain.Tickets;
using TicketHub.Domain.TicketTypes;

namespace TicketHub.Application.Tickets;

/// <summary>Đọc đơn, vé và email người mua rồi soạn sẵn email vé. Chỉ đọc, không gửi.</summary>
public sealed record GetTicketEmailQuery(Guid OrderId) : IRequest<Result<EmailMessage>>;

public static class TicketEmailErrors
{
    public static Error OrderNotPaid(Guid orderId) =>
        Error.Conflict("TicketEmail.OrderNotPaid", $"Order '{orderId}' is not paid, so it has no tickets to send.");

    public static Error RecipientNotFound(Guid userId) =>
        Error.NotFound("TicketEmail.RecipientNotFound", $"User '{userId}' does not exist, so there is no address to send to.");
}

public sealed class GetTicketEmailQueryHandler(IReadDbContext readDbContext, IUserRepository userRepository)
    : IRequestHandler<GetTicketEmailQuery, Result<EmailMessage>>
{
    public async Task<Result<EmailMessage>> Handle(GetTicketEmailQuery request, CancellationToken cancellationToken)
    {
        // Sự kiện đã xoá mềm vẫn phải hiện tên trong email: vé đã bán rồi.
        var order = await readDbContext.Query<Order>()
            .Where(o => o.Id == request.OrderId)
            .Select(o => new
            {
                o.Code,
                o.UserId,
                o.Status,
                Event = readDbContext.Query<Event>().IgnoreQueryFilters()
                    .Where(e => e.Id == o.EventId)
                    .Select(e => new { e.Name, e.Venue, e.StartsAt })
                    .First()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (order is null)
        {
            return OrderErrors.NotFound(request.OrderId);
        }

        if (order.Status != OrderStatus.Paid)
        {
            return TicketEmailErrors.OrderNotPaid(request.OrderId);
        }

        var tickets = await readDbContext.Query<Ticket>()
            .Where(t => t.OrderId == request.OrderId)
            .Select(t => new TicketLine(
                t.Code,
                readDbContext.Query<TicketType>().IgnoreQueryFilters().Where(tt => tt.Id == t.TicketTypeId).Select(tt => tt.Name).First()))
            .ToListAsync(cancellationToken);

        // Lấy email lúc gửi (không lưu trong outbox): người mua đổi email thì vé đi tới địa chỉ mới nhất.
        var user = await userRepository.GetByIdAsync(order.UserId, cancellationToken);
        if (user is null)
        {
            return TicketEmailErrors.RecipientNotFound(order.UserId);
        }

        return TicketEmailTemplate.Build(
            user.Email, user.FullName, order.Code, order.Event.Name, order.Event.Venue, order.Event.StartsAt, tickets);
    }
}

public sealed record TicketLine(string Code, string TicketTypeName);

/// <summary>Nội dung email vé. Tách riêng khỏi phần đọc DB để kiểm được bằng unit test.</summary>
public static class TicketEmailTemplate
{
    // Việt Nam không có giờ mùa hè nên cộng cố định 7 giờ là đủ, không cần dữ liệu múi giờ của hệ điều hành.
    private static readonly TimeSpan _vietnamOffset = TimeSpan.FromHours(7);

    public static EmailMessage Build(
        string toAddress, string toName, string orderCode,
        string eventName, string venue, DateTimeOffset startsAt, IReadOnlyCollection<TicketLine> tickets)
    {
        ArgumentNullException.ThrowIfNull(tickets);

        var startsAtText = startsAt.ToOffset(_vietnamOffset).ToString("HH:mm 'ngày' dd/MM/yyyy", CultureInfo.InvariantCulture);
        var ordered = tickets.OrderBy(t => t.TicketTypeName, StringComparer.Ordinal).ThenBy(t => t.Code, StringComparer.Ordinal).ToList();
        var subject = $"Vé của bạn: {eventName} (đơn {orderCode})";

        var text = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Chào {toName},")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Đơn {orderCode} đã thanh toán thành công. Đây là {ordered.Count} vé của bạn:")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Sự kiện: {eventName}")
            .AppendLine(CultureInfo.InvariantCulture, $"Địa điểm: {venue}")
            .AppendLine(CultureInfo.InvariantCulture, $"Thời gian: {startsAtText}")
            .AppendLine();
        foreach (var ticket in ordered)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- {ticket.TicketTypeName}: {ticket.Code}");
        }
        text.AppendLine().AppendLine("Xuất trình mã vé khi soát vé.");

        // Tên sự kiện, tên người... do người dùng nhập nên phải mã hoá trước khi chèn vào HTML.
        static string H(string value) => WebUtility.HtmlEncode(value);
        var html = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"<p>Chào {H(toName)},</p>")
            .Append(CultureInfo.InvariantCulture, $"<p>Đơn <b>{H(orderCode)}</b> đã thanh toán thành công. Đây là {ordered.Count} vé của bạn:</p>")
            .Append(CultureInfo.InvariantCulture, $"<p><b>{H(eventName)}</b><br>{H(venue)}<br>{H(startsAtText)}</p>")
            .Append("<table border=\"1\" cellpadding=\"6\" cellspacing=\"0\"><tr><th>Loại vé</th><th>Mã vé</th></tr>");
        foreach (var ticket in ordered)
        {
            html.Append(CultureInfo.InvariantCulture, $"<tr><td>{H(ticket.TicketTypeName)}</td><td><code>{H(ticket.Code)}</code></td></tr>");
        }
        html.Append("</table><p>Xuất trình mã vé khi soát vé.</p>");

        return new EmailMessage(toAddress, toName, subject, text.ToString(), html.ToString());
    }
}
