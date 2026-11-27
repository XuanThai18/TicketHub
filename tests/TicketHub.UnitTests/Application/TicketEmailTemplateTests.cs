using TicketHub.Application.Tickets;

namespace TicketHub.UnitTests.Application;

public sealed class TicketEmailTemplateTests
{
    // 13:00 UTC = 20:00 giờ Việt Nam.
    private static readonly DateTimeOffset _startsAt = new(2026, 12, 24, 13, 0, 0, TimeSpan.Zero);

    private static readonly TicketLine[] _tickets =
    [
        new("VIPCODE000000002", "VIP"),
        new("NORMALCODE000001", "Thường"),
        new("VIPCODE000000001", "VIP")
    ];

    [Fact]
    public void Build_ContainsRecipientEventAndEveryTicketCode()
    {
        var email = TicketEmailTemplate.Build("an@example.com", "An", "TH-0001", "Concert Noel", "Nhà hát", _startsAt, _tickets);

        Assert.Equal(("an@example.com", "An"), (email.ToAddress, email.ToName));
        Assert.Contains("Concert Noel", email.Subject, StringComparison.Ordinal);
        Assert.Contains("TH-0001", email.Subject, StringComparison.Ordinal);
        Assert.All(_tickets, t =>
        {
            Assert.Contains(t.Code, email.TextBody, StringComparison.Ordinal);
            Assert.Contains(t.Code, email.HtmlBody, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Build_ShowsStartTimeInVietnamTime()
    {
        var email = TicketEmailTemplate.Build("an@example.com", "An", "TH-0001", "Concert", "Nhà hát", _startsAt, _tickets);

        Assert.Contains("20:00 ngày 24/12/2026", email.TextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_EncodesUserTextInHtml()
    {
        // Tên sự kiện do người dùng nhập: chèn thẳng vào HTML thì chạy được script trong trình đọc thư.
        var email = TicketEmailTemplate.Build("an@example.com", "An", "TH-0001", "<script>alert(1)</script>", "Nhà hát", _startsAt, _tickets);

        Assert.DoesNotContain("<script>", email.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", email.HtmlBody, StringComparison.Ordinal);
    }
}
