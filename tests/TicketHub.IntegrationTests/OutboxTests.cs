using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using TicketHub.Api.Contracts.Common;
using TicketHub.Api.Contracts.Events;
using TicketHub.Api.Contracts.Orders;
using TicketHub.Application.Events;
using TicketHub.Application.Messaging;
using TicketHub.Domain.Common;
using TicketHub.Domain.Outbox;
using TicketHub.Infrastructure.Messaging;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.IntegrationTests;

/// <summary>
/// Outbox (Ticket 5.2): đơn Paid → tin OrderPaid nằm trong outbox → job nền gửi lên RabbitMQ thật.
/// Các test trong class chạy tuần tự, nên test "broker chết" không chen vào test luồng chính.
/// </summary>
public sealed class OutboxTests(ApiFactory factory)
{
    private const string Events = "/api/v1/events";
    private const string Orders = "/api/v1/orders";
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(20);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OrderPaid_MessageIsPublishedToTicketEmailsQueue()
    {
        var orderId = await PlaceAndPayOrderAsync();

        // Job nền chạy mỗi 2 giây: chờ tới khi tin được đánh dấu đã gửi.
        var message = await WaitForAsync(async () => await OutboxMessageAsync(orderId) is { ProcessedAt: not null } m ? m : null);

        Assert.Equal((OrderPaidMessage.Type, 0, null), (message.Type, message.Attempts, message.LastError));

        // Tin thật sự nằm trong queue ticket-emails, mang đúng MessageId = Id dòng outbox.
        var delivered = await WaitForAsync(() => FindInQueueAsync(message.Id));
        Assert.Equal(OrderPaidMessage.Type, delivered.Type);
        var payload = JsonSerializer.Deserialize<OrderPaidMessage>(delivered.Body, JsonSerializerOptions.Web);
        Assert.Equal(orderId, payload!.OrderId);
    }

    [Fact]
    public async Task BrokerDown_MessageWaitsInOutbox_ThenIsPublishedWhenBrokerIsBack()
    {
        factory.Publisher.BrokerDown = true;
        try
        {
            var orderId = await PlaceAndPayOrderAsync();

            // RabbitMQ "chết": đơn vẫn Paid bình thường, tin nằm lại outbox, job ghi nhận lỗi.
            await WaitForAsync(async () => await FailedAttemptsAsync() > 0 ? "failed" : null);
            Assert.Null((await OutboxMessageAsync(orderId))!.ProcessedAt);

            // RabbitMQ "sống lại": không ai phải làm gì, lượt quét sau tự gửi nốt.
            factory.Publisher.BrokerDown = false;
            var message = await WaitForAsync(async () => await OutboxMessageAsync(orderId) is { ProcessedAt: not null } m ? m : null);
            Assert.Null(message.LastError);
        }
        finally
        {
            factory.Publisher.BrokerDown = false;
        }
    }

    // ---------- Helpers ----------

    private async Task<Guid> PlaceAndPayOrderAsync()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var user = factory.CreateAuthenticatedClient(Roles.User);
        var ticketId = await CreateEventAsync(admin);

        using var placed = await user.PostAsJsonAsync(Orders, new { lines = new[] { new { ticketTypeId = ticketId, quantity = 1 } } }, Ct);
        placed.EnsureSuccessStatusCode();
        var orderId = (await placed.Content.ReadFromJsonAsync<CreatedResponse>(Ct))!.Id;

        using var pay = await user.PostAsync($"{Orders}/{orderId}/pay", null, Ct);
        pay.EnsureSuccessStatusCode();
        var payment = await pay.Content.ReadFromJsonAsync<PayOrderResponse>(Ct);
        (await user.PostAsync(payment!.CheckoutUrl, null, Ct)).EnsureSuccessStatusCode().Dispose(); // cổng giả lập báo đã trả tiền
        return orderId;
    }

    private static async Task<Guid> CreateEventAsync(HttpClient admin)
    {
        var now = DateTimeOffset.UtcNow;
        using var created = await admin.PostAsJsonAsync(Events, new CreateEventRequest(
            $"Concert {Guid.NewGuid():N}", null, "Nhà hát",
            now.AddDays(11), now.AddDays(11).AddHours(3), now.AddHours(-1), now.AddDays(10)), Ct);
        created.EnsureSuccessStatusCode();
        var eventId = (await created.Content.ReadFromJsonAsync<CreatedResponse>(Ct))!.Id;

        (await admin.PostAsJsonAsync($"{Events}/{eventId}/ticket-types",
            new AddTicketTypeRequest("VIP", null, 500_000m, 10, 4), Ct)).EnsureSuccessStatusCode().Dispose();
        (await admin.PostAsync($"{Events}/{eventId}/publish", null, Ct)).EnsureSuccessStatusCode().Dispose();

        var detail = await admin.GetFromJsonAsync<EventDetailResponse>($"{Events}/{eventId}", Ct);
        return detail!.TicketTypes[0].Id;
    }

    private async Task<OutboxMessage?> OutboxMessageAsync(Guid orderId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Set<OutboxMessage>()
            .FromSql($"SELECT * FROM outbox_messages WHERE payload->>'orderId' = {orderId.ToString()}")
            .AsNoTracking()
            .SingleOrDefaultAsync(Ct);
    }

    // Lúc broker chết job dừng ở tin cũ nhất (có thể của test khác), nên đếm lỗi trên mọi tin chưa gửi.
    private async Task<int> FailedAttemptsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Set<OutboxMessage>().Where(m => m.ProcessedAt == null).SumAsync(m => m.Attempts, Ct);
    }

    // Đọc (và lấy luôn ra khỏi queue) từng tin cho tới khi gặp tin có MessageId cần tìm.
    private async Task<QueuedMessage?> FindInQueueAsync(Guid messageId)
    {
        var connectionFactory = new ConnectionFactory { Uri = new Uri(factory.RabbitMqConnectionString) };
        await using var connection = await connectionFactory.CreateConnectionAsync(Ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: Ct);

        while (await channel.BasicGetAsync(RabbitMqTopology.TicketEmailsQueue, autoAck: true, Ct) is { } result)
        {
            if (result.BasicProperties.MessageId == messageId.ToString())
            {
                return new QueuedMessage(result.BasicProperties.Type, Encoding.UTF8.GetString(result.Body.Span));
            }
        }

        return null;
    }

    // Hỏi lại mỗi 250 ms cho tới khi probe trả khác null (job nền chạy theo nhịp, không biết chính xác lúc nào xong).
    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> probe) where T : class
    {
        var deadline = DateTimeOffset.UtcNow + _timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            await Task.Delay(250, Ct);
        }

        throw new TimeoutException($"Condition not met within {_timeout.TotalSeconds}s.");
    }

    private sealed record QueuedMessage(string? Type, string Body);
}
