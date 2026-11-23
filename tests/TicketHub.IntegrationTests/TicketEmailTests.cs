using System.Net.Http.Json;
using System.Text.Json;
using TicketHub.Api.Contracts.Auth;
using TicketHub.Api.Contracts.Common;
using TicketHub.Api.Contracts.Events;
using TicketHub.Api.Contracts.Orders;
using TicketHub.Application.Events;
using TicketHub.Application.Orders;
using TicketHub.Domain.Common;

namespace TicketHub.IntegrationTests;

/// <summary>
/// Email vé (Ticket 5.3), đi hết đường thật: thanh toán → outbox → RabbitMQ → consumer → SMTP → Mailpit.
/// Kiểm bằng HTTP API của Mailpit: thư tới đúng người mua, có đủ mã vé.
/// </summary>
public sealed class TicketEmailTests(ApiFactory factory)
{
    private const string Events = "/api/v1/events";
    private const string Orders = "/api/v1/orders";
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OrderPaid_BuyerReceivesOneEmailWithEveryTicketCode()
    {
        // Người mua phải là user thật (có email trong bảng users), không phải token tự sinh.
        var (buyer, email) = await CreateBuyerAsync();
        using var _ = buyer;
        var orderId = await PlaceAndPayOrderAsync(buyer, quantity: 3);
        var order = await buyer.GetFromJsonAsync<OrderDetailResponse>($"{Orders}/{orderId}", Ct);

        var messageId = await WaitForEmailAsync(email);
        using var mail = await GetJsonAsync($"/api/v1/message/{messageId}");
        var subject = mail.RootElement.GetProperty("Subject").GetString()!;
        var text = mail.RootElement.GetProperty("Text").GetString()!;

        Assert.Contains(order!.Code, subject, StringComparison.Ordinal);
        Assert.Equal(3, order.Tickets.Count);
        Assert.All(order.Tickets, t => Assert.Contains(t.Code, text, StringComparison.Ordinal));

        // Đợi thêm vài lượt quét: vẫn chỉ đúng một email, không gửi lặp.
        await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(1, await CountEmailsAsync(email));
    }

    // ---------- Helpers ----------

    private async Task<(HttpClient Client, string Email)> CreateBuyerAsync()
    {
        var email = $"buyer-{Guid.NewGuid():N}@example.com";
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var response = await admin.PostAsJsonAsync("/api/v1/auth/users",
            new CreateUserRequest(email, "Người Mua", "Passw0rd!", [Roles.User]), Ct);
        response.EnsureSuccessStatusCode();
        var userId = (await response.Content.ReadFromJsonAsync<CreatedUserResponse>(Ct))!.Id;
        return (factory.CreateAuthenticatedClient(userId, Roles.User), email);
    }

    private async Task<Guid> PlaceAndPayOrderAsync(HttpClient buyer, int quantity)
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var now = DateTimeOffset.UtcNow;
        using var created = await admin.PostAsJsonAsync(Events, new CreateEventRequest(
            $"Concert {Guid.NewGuid():N}", null, "Nhà hát",
            now.AddDays(11), now.AddDays(11).AddHours(3), now.AddHours(-1), now.AddDays(10)), Ct);
        created.EnsureSuccessStatusCode();
        var eventId = (await created.Content.ReadFromJsonAsync<CreatedResponse>(Ct))!.Id;
        (await admin.PostAsJsonAsync($"{Events}/{eventId}/ticket-types",
            new AddTicketTypeRequest("VIP", null, 500_000m, 10, 4), Ct)).EnsureSuccessStatusCode().Dispose();
        (await admin.PostAsync($"{Events}/{eventId}/publish", null, Ct)).EnsureSuccessStatusCode().Dispose();
        var ticketTypeId = (await admin.GetFromJsonAsync<EventDetailResponse>($"{Events}/{eventId}", Ct))!.TicketTypes[0].Id;

        using var placed = await buyer.PostAsJsonAsync(Orders, new { lines = new[] { new { ticketTypeId, quantity } } }, Ct);
        placed.EnsureSuccessStatusCode();
        var orderId = (await placed.Content.ReadFromJsonAsync<CreatedResponse>(Ct))!.Id;

        using var pay = await buyer.PostAsync($"{Orders}/{orderId}/pay", null, Ct);
        pay.EnsureSuccessStatusCode();
        var payment = await pay.Content.ReadFromJsonAsync<PayOrderResponse>(Ct);
        (await buyer.PostAsync(payment!.CheckoutUrl, null, Ct)).EnsureSuccessStatusCode().Dispose();
        return orderId;
    }

    // Hỏi Mailpit mỗi 500 ms tới khi có thư gửi tới địa chỉ này; trả về Id thư.
    private async Task<string> WaitForEmailAsync(string email)
    {
        var deadline = DateTimeOffset.UtcNow + _timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var search = await SearchAsync(email);
            var messages = search.RootElement.GetProperty("messages");
            if (messages.GetArrayLength() > 0)
            {
                return messages[0].GetProperty("ID").GetString()!;
            }

            await Task.Delay(500, Ct);
        }

        throw new TimeoutException($"No email to {email} within {_timeout.TotalSeconds}s.");
    }

    private async Task<int> CountEmailsAsync(string email)
    {
        using var search = await SearchAsync(email);
        return search.RootElement.GetProperty("messages").GetArrayLength();
    }

    private Task<JsonDocument> SearchAsync(string email) =>
        GetJsonAsync($"/api/v1/search?query={Uri.EscapeDataString($"to:\"{email}\"")}");

    private async Task<JsonDocument> GetJsonAsync(string path)
    {
        using var http = new HttpClient { BaseAddress = factory.MailpitApi };
        await using var stream = await http.GetStreamAsync(new Uri(path, UriKind.Relative), Ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: Ct);
    }
}
