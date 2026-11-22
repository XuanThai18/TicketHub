using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketHub.Api.Contracts.Common;
using TicketHub.Api.Contracts.Events;
using TicketHub.Api.Contracts.Orders;
using TicketHub.Application.Events;
using TicketHub.Application.Orders;
using TicketHub.Application.Common.Paging;
using TicketHub.Application.Payments;
using TicketHub.Application.Tickets;
using TicketHub.Domain.Common;
using TicketHub.Domain.Orders;
using TicketHub.Domain.Payments;
using TicketHub.Domain.Tickets;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.IntegrationTests;

/// <summary>Thanh toán (Ticket 4.2): mở lượt thanh toán, webhook có chữ ký HMAC, gửi lặp, đua với job hết hạn.</summary>
public sealed class PaymentEndpointsTests(ApiFactory factory)
{
    private const string Events = "/api/v1/events";
    private const string Orders = "/api/v1/orders";
    private const string Webhook = "/api/v1/payments/webhook";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // ---------- Luồng chính ----------

    [Fact]
    public async Task Pay_ThenGatewaySucceeds_OrderBecomesPaid()
    {
        var (admin, user, eventId, ticketId) = await ArrangeAsync();
        using var _ = admin;
        using var __ = user;
        var orderId = await PlaceOrderAsync(user, ticketId, 2);

        using var pay = await user.PostAsync($"{Orders}/{orderId}/pay", null, Ct);
        var payment = await pay.Content.ReadFromJsonAsync<PayOrderResponse>(Ct);
        using var completed = await user.PostAsync(payment!.CheckoutUrl, null, Ct); // "khách trả tiền" ở cổng giả lập

        Assert.Equal(HttpStatusCode.Created, pay.StatusCode);
        Assert.Equal(1_000_000m, payment.Amount);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var order = await user.GetFromJsonAsync<OrderDetailResponse>($"{Orders}/{orderId}", Ct);
        Assert.Equal("Paid", order!.Status);
        Assert.NotNull(order.PaidAt);
        Assert.Equal(PaymentStatus.Succeeded, (await PaymentAsync(payment.PaymentId)).Status);
        Assert.Equal(8, await AvailableAsync(admin, eventId, ticketId));

        // 2 chỗ → 2 vé hợp lệ, mã khác nhau, hiện trong chi tiết đơn.
        Assert.Equal(2, order.Tickets.Count);
        Assert.All(order.Tickets, t => Assert.Equal(("Valid", ticketId), (t.Status, t.TicketTypeId)));
        Assert.Equal(2, order.Tickets.Select(t => t.Code).Distinct().Count());
    }

    [Fact]
    public async Task ListMyTickets_ShowsOnlyOwnTicketsWithEventInfo()
    {
        var (admin, buyer, eventId, ticketId) = await ArrangeAsync();
        using var a = admin;
        using var b = buyer;
        using var other = factory.CreateAuthenticatedClient(Roles.User);
        var orderId = await PlaceOrderAsync(buyer, ticketId, 3);
        (await buyer.PostAsync((await PayAsync(buyer, orderId)).CheckoutUrl, null, Ct)).Dispose();

        var mine = await buyer.GetFromJsonAsync<PagedResult<MyTicketResponse>>("/api/v1/tickets/my", Ct);
        var theirs = await other.GetFromJsonAsync<PagedResult<MyTicketResponse>>("/api/v1/tickets/my", Ct);

        Assert.Equal(3, mine!.TotalCount);
        Assert.All(mine.Items, t => Assert.Equal((orderId, eventId, "VIP"), (t.OrderId, t.EventId, t.TicketTypeName)));
        Assert.Equal(0, theirs!.TotalCount);
    }

    [Fact]
    public async Task Pay_GatewayFails_OrderStaysPendingAndCanPayAgain()
    {
        var (admin, user, _, ticketId) = await ArrangeAsync();
        using var a = admin;
        using var u = user;
        var orderId = await PlaceOrderAsync(user, ticketId, 1);
        var first = await PayAsync(user, orderId);

        (await user.PostAsync($"{first.CheckoutUrl}?outcome=failed", null, Ct)).Dispose();
        using var retry = await user.PostAsync($"{Orders}/{orderId}/pay", null, Ct);
        var second = await retry.Content.ReadFromJsonAsync<PayOrderResponse>(Ct);

        Assert.Equal(PaymentStatus.Failed, (await PaymentAsync(first.PaymentId)).Status);
        Assert.Equal(OrderStatus.Pending, await OrderStatusAsync(orderId));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.NotEqual(first.PaymentId, second!.PaymentId);
    }

    // ---------- Mở lượt thanh toán ----------

    [Fact]
    public async Task Pay_ConcurrentClicks_ShareOnePendingPayment()
    {
        // Bấm "Thanh toán" 10 lần cùng lúc: chỉ một lượt Pending, ai cũng nhận lại đúng lượt đó.
        var (admin, user, _, ticketId) = await ArrangeAsync();
        using var a = admin;
        using var u = user;
        var orderId = await PlaceOrderAsync(user, ticketId, 1);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => user.PostAsync($"{Orders}/{orderId}/pay", null, Ct)));
        var ids = new HashSet<Guid>();
        foreach (var response in responses)
        {
            Assert.True(response.IsSuccessStatusCode, $"Got {(int)response.StatusCode}");
            ids.Add((await response.Content.ReadFromJsonAsync<PayOrderResponse>(Ct))!.PaymentId);
            response.Dispose();
        }

        Assert.Single(ids);
        Assert.Equal(1, await PaymentCountAsync(orderId));
    }

    [Fact]
    public async Task Pay_OtherUsersOrder_Returns404()
    {
        var (admin, owner, _, ticketId) = await ArrangeAsync();
        using var a = admin;
        using var o = owner;
        using var stranger = factory.CreateAuthenticatedClient(Roles.User);
        var orderId = await PlaceOrderAsync(owner, ticketId, 1);

        using var response = await stranger.PostAsync($"{Orders}/{orderId}/pay", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Pay_CancelledOrPastExpiry_Returns409NotPayable()
    {
        var (admin, user, _, ticketId) = await ArrangeAsync();
        using var a = admin;
        using var u = user;
        var cancelled = await PlaceOrderAsync(user, ticketId, 1);
        (await user.PostAsync($"{Orders}/{cancelled}/cancel", null, Ct)).Dispose();
        var overdue = await PlaceOrderAsync(user, ticketId, 1);
        await MovePastExpiryAsync(overdue); // còn Pending vì job chưa chạy, nhưng đã quá giờ

        using var payCancelled = await user.PostAsync($"{Orders}/{cancelled}/pay", null, Ct);
        using var payOverdue = await user.PostAsync($"{Orders}/{overdue}/pay", null, Ct);

        Assert.Equal((HttpStatusCode.Conflict, "Orders.NotPayable"), (payCancelled.StatusCode, await CodeAsync(payCancelled)));
        Assert.Equal((HttpStatusCode.Conflict, "Orders.NotPayable"), (payOverdue.StatusCode, await CodeAsync(payOverdue)));
    }

    // ---------- Webhook: chữ ký ----------

    [Fact]
    public async Task Webhook_BadSignature_Returns401AndChangesNothing()
    {
        var (admin, user, _, ticketId) = await ArrangeAsync();
        using var a = admin;
        using var u = user;
        var orderId = await PlaceOrderAsync(user, ticketId, 1);
        var payment = await PayAsync(user, orderId);
        using var anonymous = factory.CreateClient();
        var body = WebhookBody(await TransactionIdAsync(payment.PaymentId), "succeeded");

        using var response = await anonymous.SendAsync(WebhookRequest(body, DateTimeOffset.UtcNow, secret: "attacker-guessed-secret-0123456789abcd"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Payments.InvalidSignature", await CodeAsync(response));
        Assert.Equal(OrderStatus.Pending, await OrderStatusAsync(orderId));
        Assert.Equal(PaymentStatus.Pending, (await PaymentAsync(payment.PaymentId)).Status);
    }

    [Fact]
    public async Task Webhook_CorrectlySignedButTenMinutesOld_Returns401Expired()
    {
        var (admin, user, _, ticketId) = await ArrangeAsync();
        using var a = admin;
        using var u = user;
        var orderId = await PlaceOrderAsync(user, ticketId, 1);
        var payment = await PayAsync(user, orderId);
        using var anonymous = factory.CreateClient();
        var body = WebhookBody(await TransactionIdAsync(payment.PaymentId), "succeeded");

        using var response = await anonymous.SendAsync(WebhookRequest(body, DateTimeOffset.UtcNow.AddMinutes(-10)), Ct);

        Assert.Equal("Payments.SignatureExpired", await CodeAsync(response));
        Assert.Equal(OrderStatus.Pending, await OrderStatusAsync(orderId));
    }

    // ---------- Webhook: gửi lặp ----------

    [Fact]
    public async Task Webhook_SameNotificationTenTimesConcurrently_ProcessedOnce()
    {
        // Cổng gửi lặp cùng lúc. Nếu chỉ dựa vào "đơn còn Pending" thì lần thứ hai thấy đơn đã Paid
        // và đánh dấu nhầm RefundRequired; lượt thanh toán phải được chốt trước để chặn chuyện này.
        var (admin, user, _, ticketId) = await ArrangeAsync();
        using var a = admin;
        using var u = user;
        var orderId = await PlaceOrderAsync(user, ticketId, 1);
        var payment = await PayAsync(user, orderId);
        using var anonymous = factory.CreateClient();
        var body = WebhookBody(await TransactionIdAsync(payment.PaymentId), "succeeded");
        var now = DateTimeOffset.UtcNow;

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => anonymous.SendAsync(WebhookRequest(body, now), Ct)));
        var statuses = responses.Select(r => r.StatusCode).Distinct().ToList();
        foreach (var response in responses)
        {
            response.Dispose();
        }

        Assert.Equal([HttpStatusCode.OK], statuses);
        Assert.Equal(OrderStatus.Paid, await OrderStatusAsync(orderId));
        Assert.Equal(PaymentStatus.Succeeded, (await PaymentAsync(payment.PaymentId)).Status);
        Assert.Equal(1, await TicketCountAsync(orderId)); // 10 webhook, đơn 1 chỗ → đúng 1 vé
    }

    // ---------- Webhook đua với job hết hạn ----------

    [Fact]
    public async Task Webhook_AfterExpiryJobWon_MarksRefundRequiredAndKeepsStock()
    {
        var (admin, user, eventId, ticketId) = await ArrangeAsync();
        using var a = admin;
        using var u = user;
        var orderId = await PlaceOrderAsync(user, ticketId, 2);
        var payment = await PayAsync(user, orderId);
        await MovePastExpiryAsync(orderId);
        await SweepExpiredAsync(); // job thắng: đơn Expired, 2 vé về kho

        using var completed = await user.PostAsync(payment.CheckoutUrl, null, Ct); // tiền về muộn

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(OrderStatus.Expired, await OrderStatusAsync(orderId));
        Assert.Equal(PaymentStatus.RefundRequired, (await PaymentAsync(payment.PaymentId)).Status);
        Assert.Equal(10, await AvailableAsync(admin, eventId, ticketId)); // vé không bị giữ lại cho đơn đã hết hạn
        Assert.Equal(0, await TicketCountAsync(orderId)); // không phát vé cho đơn đã hết hạn
    }

    [Fact]
    public async Task Webhook_BeforeExpiryJob_OrderStaysPaid()
    {
        // Đã quá giờ vài giây nhưng job chưa chạy: vé vẫn đang giữ cho đơn, nhận tiền. Job chạy sau thì bỏ qua.
        var (admin, user, eventId, ticketId) = await ArrangeAsync();
        using var a = admin;
        using var u = user;
        var orderId = await PlaceOrderAsync(user, ticketId, 2);
        var payment = await PayAsync(user, orderId);
        await MovePastExpiryAsync(orderId);

        (await user.PostAsync(payment.CheckoutUrl, null, Ct)).Dispose();
        await SweepExpiredAsync();

        Assert.Equal(OrderStatus.Paid, await OrderStatusAsync(orderId));
        Assert.Equal(PaymentStatus.Succeeded, (await PaymentAsync(payment.PaymentId)).Status);
        Assert.Equal(8, await AvailableAsync(admin, eventId, ticketId));
    }

    // ---------- Webhook: đầu vào sai ----------

    [Fact]
    public async Task Webhook_UnknownTransaction_Returns404()
    {
        using var anonymous = factory.CreateClient();
        var body = WebhookBody("mock_does_not_exist", "succeeded");

        using var response = await anonymous.SendAsync(WebhookRequest(body, DateTimeOffset.UtcNow), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MockGateway_UnknownOutcome_Returns400()
    {
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.PostAsync("/api/v1/mock-gateway/checkout/mock_x/complete?outcome=maybe", null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- Hàm phụ ----------

    private async Task<(HttpClient Admin, HttpClient User, Guid EventId, Guid TicketId)> ArrangeAsync()
    {
        var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var user = factory.CreateAuthenticatedClient(Roles.User);
        var (eventId, ticketId) = await CreateEventAsync(admin);
        return (admin, user, eventId, ticketId);
    }

    private static string WebhookBody(string transactionId, string status) =>
        JsonSerializer.Serialize(new PaymentWebhookPayload(transactionId, status, DateTimeOffset.UtcNow), _json);

    // Giả làm cổng: ký body bằng khoá (mặc định là khoá đúng của test).
    private static HttpRequestMessage WebhookRequest(string body, DateTimeOffset sentAt, string secret = ApiFactory.WebhookSecret)
    {
        var timestamp = sentAt.ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, Webhook) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add(PaymentSignature.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(PaymentSignature.SignatureHeader, PaymentSignature.Compute(secret, timestamp, body));
        return request;
    }

    private static async Task<PayOrderResponse> PayAsync(HttpClient user, Guid orderId)
    {
        using var response = await user.PostAsync($"{Orders}/{orderId}/pay", null, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PayOrderResponse>(Ct))!;
    }

    private static async Task<Guid> PlaceOrderAsync(HttpClient user, Guid ticketId, int quantity)
    {
        using var response = await user.PostAsJsonAsync(Orders, new { lines = new[] { new { ticketTypeId = ticketId, quantity } } }, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedResponse>(Ct))!.Id;
    }

    private static async Task<(Guid EventId, Guid TicketTypeId)> CreateEventAsync(HttpClient admin)
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
        return (eventId, detail!.TicketTypes[0].Id);
    }

    private static async Task<int> AvailableAsync(HttpClient admin, Guid eventId, Guid ticketTypeId) =>
        (await admin.GetFromJsonAsync<EventDetailResponse>($"{Events}/{eventId}", Ct))!
            .TicketTypes.Single(t => t.Id == ticketTypeId).AvailableQuantity;

    private async Task MovePastExpiryAsync(Guid orderId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Set<Order>().Where(o => o.Id == orderId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct);
    }

    private async Task SweepExpiredAsync()
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ExpireDueOrdersCommand(BatchSize: 100), Ct);
    }

    private async Task<OrderStatus> OrderStatusAsync(Guid orderId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Set<Order>().AsNoTracking().Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync(Ct);
    }

    private async Task<Payment> PaymentAsync(Guid paymentId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Set<Payment>().AsNoTracking().SingleAsync(p => p.Id == paymentId, Ct);
    }

    private async Task<string> TransactionIdAsync(Guid paymentId) => (await PaymentAsync(paymentId)).ProviderTransactionId;

    private async Task<int> TicketCountAsync(Guid orderId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Set<Ticket>().CountAsync(t => t.OrderId == orderId, Ct);
    }

    private async Task<int> PaymentCountAsync(Guid orderId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Set<Payment>().CountAsync(p => p.OrderId == orderId, Ct);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
