using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using TicketHub.Api.Contracts.Auth;
using TicketHub.Api.Contracts.Common;
using TicketHub.Api.Contracts.Events;
using TicketHub.Api.Contracts.Orders;
using TicketHub.Application.Events;
using TicketHub.Application.Orders;
using TicketHub.Domain.Common;
using TicketHub.Domain.Outbox;
using TicketHub.Infrastructure.Messaging;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.IntegrationTests;

/// <summary>
/// Email vé (Ticket 5.3–5.4), đi hết đường thật: thanh toán → outbox → RabbitMQ → consumer → SMTP → Mailpit.
/// Kiểm bằng HTTP API của Mailpit: thư tới đúng người mua, có đủ mã vé, không gửi lặp, lỗi tạm thời được thử lại.
/// </summary>
public sealed class TicketEmailTests(ApiFactory factory)
{
    private const string Events = "/api/v1/events";
    private const string Orders = "/api/v1/orders";
    private const string RetryCountHeader = "x-retry-count"; // = TicketEmailConsumer.RetryCountHeader (internal)
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

    [Fact]
    public async Task SameMessageDeliveredTwice_EmailIsSentOnlyOnce()
    {
        var (buyer, email) = await CreateBuyerAsync();
        using var _ = buyer;
        var firstOrderId = await PlaceAndPayOrderAsync(buyer, quantity: 1);
        await WaitForEmailAsync(email);

        // Giả lập RabbitMQ giao lặp: đẩy thêm một bản y hệt (cùng MessageId) vào exchange.
        await PublishCopyAsync(await OutboxMessageAsync(firstOrderId));

        // Đơn thứ hai đi qua outbox nên vào queue SAU bản lặp; consumer xử lý lần lượt,
        // nên khi thư đơn hai tới thì bản lặp chắc chắn đã được xử lý xong.
        // Phải chờ đúng thư của đơn hai (theo mã đơn): chờ "đủ 2 thư" thì thư thứ hai có thể chính là thư gửi lặp.
        var secondOrderId = await PlaceAndPayOrderAsync(buyer, quantity: 1);
        var secondOrder = await buyer.GetFromJsonAsync<OrderDetailResponse>($"{Orders}/{secondOrderId}", Ct);
        (await WaitForEmailCountAsync(email, 1, subject: secondOrder!.Code)).Dispose();

        Assert.Equal(2, factory.EmailSender.Attempts(email)); // bản lặp không hề thử gửi
        Assert.Equal(2, await CountEmailsAsync(email));
    }

    [Fact]
    public async Task SmtpFailsTwice_MessageIsRetried_ThenEmailIsSent()
    {
        var (buyer, email) = await CreateBuyerAsync();
        using var _ = buyer;
        factory.EmailSender.FailNext(email, times: 2);

        await PlaceAndPayOrderAsync(buyer, quantity: 1);
        await WaitForEmailAsync(email);

        Assert.Equal(3, factory.EmailSender.Attempts(email)); // lỗi, lỗi, thành công
        Assert.Equal(1, await CountEmailsAsync(email));
    }

    [Fact]
    public async Task SmtpKeepsFailing_MessageGoesToDeadLetterAfterMaxRetries()
    {
        var (buyer, email) = await CreateBuyerAsync();
        using var _ = buyer;
        factory.EmailSender.FailNext(email, times: int.MaxValue);

        var orderId = await PlaceAndPayOrderAsync(buyer, quantity: 1);
        var outbox = await OutboxMessageAsync(orderId);

        var deadLetter = await WaitForDeadLetterAsync(outbox.Id.ToString());
        Assert.Equal(ApiFactory.TicketEmailMaxRetries,
            Convert.ToInt32(deadLetter.Headers![RetryCountHeader], CultureInfo.InvariantCulture));
        Assert.Equal(ApiFactory.TicketEmailMaxRetries + 1, factory.EmailSender.Attempts(email)); // lần đầu + các lần thử lại
        Assert.Equal(0, await CountEmailsAsync(email));
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

    // Dòng outbox được ghi cùng transaction với lúc đơn chuyển Paid, nên có ngay sau khi thanh toán xong.
    private async Task<OutboxMessage> OutboxMessageAsync(Guid orderId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Set<OutboxMessage>()
            .FromSql($"SELECT * FROM outbox_messages WHERE payload->>'orderId' = {orderId.ToString()}")
            .AsNoTracking()
            .SingleAsync(Ct);
    }

    private async Task PublishCopyAsync(OutboxMessage message)
    {
        await using var connection = await RabbitMqConnectionFactory().CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), Ct);
        var properties = new BasicProperties
        {
            MessageId = message.Id.ToString(),
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent
        };
        await channel.BasicPublishAsync(RabbitMqTopology.Exchange, message.Type, mandatory: true,
            properties, Encoding.UTF8.GetBytes(message.Payload), Ct);
    }

    // Đọc queue dead-letter tới khi gặp tin cần tìm. Tin của test khác (người mua giả) cũng nằm đây: lấy ra rồi bỏ qua.
    private async Task<IReadOnlyBasicProperties> WaitForDeadLetterAsync(string messageId)
    {
        await using var connection = await RabbitMqConnectionFactory().CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);

        var deadline = DateTimeOffset.UtcNow + _timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await channel.BasicGetAsync(RabbitMqTopology.TicketEmailsDeadLetterQueue, autoAck: true, Ct);
            if (result is null)
            {
                await Task.Delay(250, Ct);
            }
            else if (result.BasicProperties.MessageId == messageId)
            {
                return result.BasicProperties;
            }
        }

        throw new TimeoutException($"Message {messageId} did not reach the dead-letter queue within {_timeout.TotalSeconds}s.");
    }

    private ConnectionFactory RabbitMqConnectionFactory() => new() { Uri = new Uri(factory.RabbitMqConnectionString) };

    // Hỏi Mailpit mỗi 500 ms tới khi có thư gửi tới địa chỉ này; trả về Id thư.
    private async Task<string> WaitForEmailAsync(string email)
    {
        using var search = await WaitForEmailCountAsync(email, 1);
        return search.RootElement.GetProperty("messages")[0].GetProperty("ID").GetString()!;
    }

    private async Task<JsonDocument> WaitForEmailCountAsync(string email, int count, string? subject = null)
    {
        var deadline = DateTimeOffset.UtcNow + _timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var search = await SearchAsync(email, subject);
            if (search.RootElement.GetProperty("messages").GetArrayLength() >= count)
            {
                return search;
            }

            search.Dispose();
            await Task.Delay(500, Ct);
        }

        throw new TimeoutException($"Fewer than {count} email(s) to {email} within {_timeout.TotalSeconds}s.");
    }

    private async Task<int> CountEmailsAsync(string email)
    {
        using var search = await SearchAsync(email);
        return search.RootElement.GetProperty("messages").GetArrayLength();
    }

    // Cú pháp tìm của Mailpit: to:"địa chỉ" subject:"chữ trong tiêu đề".
    private Task<JsonDocument> SearchAsync(string email, string? subject = null)
    {
        var query = subject is null ? $"to:\"{email}\"" : $"to:\"{email}\" subject:\"{subject}\"";
        return GetJsonAsync($"/api/v1/search?query={Uri.EscapeDataString(query)}");
    }

    private async Task<JsonDocument> GetJsonAsync(string path)
    {
        using var http = new HttpClient { BaseAddress = factory.MailpitApi };
        await using var stream = await http.GetStreamAsync(new Uri(path, UriKind.Relative), Ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: Ct);
    }
}
