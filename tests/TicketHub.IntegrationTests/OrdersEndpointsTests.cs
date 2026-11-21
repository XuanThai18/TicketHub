using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketHub.Api.Contracts.Common;
using TicketHub.Api.Contracts.Events;
using TicketHub.Application.Common.Paging;
using TicketHub.Application.Events;
using TicketHub.Application.Orders;
using TicketHub.Domain.Common;
using TicketHub.Domain.OrderItems;
using TicketHub.Domain.Orders;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.IntegrationTests;

public sealed class OrdersEndpointsTests(ApiFactory factory)
{
    private const string Orders = "/api/v1/orders";
    private const string Events = "/api/v1/events";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PlaceOrder_WithoutToken_Returns401()
    {
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.PostAsJsonAsync(Orders, Lines((Guid.NewGuid(), 1)), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PlaceOrder_ValidLines_Returns201AndReducesAvailable()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var user = factory.CreateAuthenticatedClient(Roles.User);
        var (eventId, ticketId) = await CreateEventAsync(admin, total: 10, maxPerOrder: 4, publish: true);

        using var response = await user.PostAsJsonAsync(Orders, Lines((ticketId, 2)), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(8, await AvailableAsync(admin, eventId, ticketId));
    }

    [Fact]
    public async Task PlaceOrder_NotEnoughTickets_Returns409AndKeepsStock()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var first = factory.CreateAuthenticatedClient(Roles.User);
        using var second = factory.CreateAuthenticatedClient(Roles.User);
        var (eventId, ticketId) = await CreateEventAsync(admin, total: 2, maxPerOrder: 2, publish: true);
        (await first.PostAsJsonAsync(Orders, Lines((ticketId, 2)), Ct)).Dispose(); // bán hết

        using var response = await second.PostAsJsonAsync(Orders, Lines((ticketId, 1)), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("TicketTypes.SoldOut", await CodeAsync(response));
        Assert.Equal(0, await AvailableAsync(admin, eventId, ticketId));
    }

    [Fact]
    public async Task PlaceOrder_EventNotPublished_Returns409NotOnSale()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var user = factory.CreateAuthenticatedClient(Roles.User);
        var (_, ticketId) = await CreateEventAsync(admin, total: 10, maxPerOrder: 4, publish: false);

        using var response = await user.PostAsJsonAsync(Orders, Lines((ticketId, 1)), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Orders.NotOnSale", await CodeAsync(response));
    }

    // ---------- Huỷ đơn ----------

    [Fact]
    public async Task Cancel_WithoutToken_Returns401()
    {
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.PostAsync($"{Orders}/{Guid.NewGuid()}/cancel", null, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_OwnPendingOrder_Returns204AndReturnsStock()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var user = factory.CreateAuthenticatedClient(Roles.User);
        var (eventId, ticketId) = await CreateEventAsync(admin, total: 10, maxPerOrder: 4, publish: true);
        var orderId = await PlaceOrderAsync(user, (ticketId, 2));

        using var response = await user.PostAsync($"{Orders}/{orderId}/cancel", null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(10, await AvailableAsync(admin, eventId, ticketId));
    }

    [Fact]
    public async Task Cancel_OtherUsersOrder_Returns404AndKeepsStock()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var owner = factory.CreateAuthenticatedClient(Roles.User);
        using var stranger = factory.CreateAuthenticatedClient(Roles.User);
        var (eventId, ticketId) = await CreateEventAsync(admin, total: 10, maxPerOrder: 4, publish: true);
        var orderId = await PlaceOrderAsync(owner, (ticketId, 2));

        using var response = await stranger.PostAsync($"{Orders}/{orderId}/cancel", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Orders.NotFound", await CodeAsync(response));
        Assert.Equal(8, await AvailableAsync(admin, eventId, ticketId));
    }

    [Fact]
    public async Task Cancel_TwiceReturns409AndReturnsStockOnlyOnce()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var user = factory.CreateAuthenticatedClient(Roles.User);
        var (eventId, ticketId) = await CreateEventAsync(admin, total: 10, maxPerOrder: 4, publish: true);
        var orderId = await PlaceOrderAsync(user, (ticketId, 2));
        (await user.PostAsync($"{Orders}/{orderId}/cancel", null, Ct)).Dispose();

        using var second = await user.PostAsync($"{Orders}/{orderId}/cancel", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("Orders.NotCancellable", await CodeAsync(second));
        Assert.Equal(10, await AvailableAsync(admin, eventId, ticketId));
    }

    [Fact]
    public async Task Cancel_ConcurrentRequests_OnlyOneSucceedsAndStockReturnsOnce()
    {
        // Hai luồng huỷ cùng lúc: đúng một luồng được 204, luồng kia 409, và vé chỉ cộng lại một lần.
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var userId = Guid.NewGuid();
        using var first = factory.CreateAuthenticatedClient(userId, Roles.User);
        using var second = factory.CreateAuthenticatedClient(userId, Roles.User);
        var (eventId, ticketId) = await CreateEventAsync(admin, total: 10, maxPerOrder: 4, publish: true);
        var orderId = await PlaceOrderAsync(first, (ticketId, 2));

        var responses = await Task.WhenAll(
            first.PostAsync($"{Orders}/{orderId}/cancel", null, Ct),
            second.PostAsync($"{Orders}/{orderId}/cancel", null, Ct));

        try
        {
            var statuses = responses.Select(r => r.StatusCode).OrderBy(s => s).ToArray();
            Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Conflict], statuses); // 204 < 409
            Assert.Equal(10, await AvailableAsync(admin, eventId, ticketId));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    // ---------- Xem đơn ----------

    [Fact]
    public async Task GetById_WithoutToken_Returns401()
    {
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.GetAsync($"{Orders}/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetById_Owner_Returns200WithItems()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var user = factory.CreateAuthenticatedClient(Roles.User);
        var (eventId, ticketId) = await CreateEventAsync(admin, total: 10, maxPerOrder: 4, publish: true);
        var orderId = await PlaceOrderAsync(user, (ticketId, 2));

        var order = await user.GetFromJsonAsync<OrderDetailResponse>($"{Orders}/{orderId}", Ct);

        Assert.NotNull(order);
        Assert.Equal(orderId, order.Id);
        Assert.Equal(eventId, order.EventId);
        Assert.Equal("Pending", order.Status);
        Assert.Equal(1_000_000m, order.TotalAmount);
        var item = Assert.Single(order.Items);
        Assert.Equal(ticketId, item.TicketTypeId);
        Assert.Equal("VIP", item.TicketTypeName);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(1_000_000m, item.Subtotal);
    }

    [Fact]
    public async Task GetById_OtherUsersOrder_Returns404()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var owner = factory.CreateAuthenticatedClient(Roles.User);
        using var stranger = factory.CreateAuthenticatedClient(Roles.User);
        var (_, ticketId) = await CreateEventAsync(admin, total: 10, maxPerOrder: 4, publish: true);
        var orderId = await PlaceOrderAsync(owner, (ticketId, 1));

        using var response = await stranger.GetAsync($"{Orders}/{orderId}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Orders.NotFound", await CodeAsync(response));
    }

    [Fact]
    public async Task GetById_Admin_CanSeeAnyOrder()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var owner = factory.CreateAuthenticatedClient(Roles.User);
        var (_, ticketId) = await CreateEventAsync(admin, total: 10, maxPerOrder: 4, publish: true);
        var orderId = await PlaceOrderAsync(owner, (ticketId, 1));

        using var response = await admin.GetAsync($"{Orders}/{orderId}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ListMy_ReturnsOnlyOwnOrdersNewestFirst()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var user = factory.CreateAuthenticatedClient(Roles.User);
        using var other = factory.CreateAuthenticatedClient(Roles.User);
        var (_, ticketId) = await CreateEventAsync(admin, total: 10, maxPerOrder: 4, publish: true);
        var first = await PlaceOrderAsync(user, (ticketId, 1));
        var second = await PlaceOrderAsync(user, (ticketId, 2));
        await PlaceOrderAsync(other, (ticketId, 1));

        var page = await user.GetFromJsonAsync<PagedResult<OrderSummaryResponse>>($"{Orders}/my", Ct);

        Assert.NotNull(page);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(new[] { second, first }, page.Items.Select(o => o.Id));
        Assert.Equal(2, page.Items[0].TicketCount);
    }

    [Fact]
    public async Task ListMy_InvalidPageSize_Returns400()
    {
        using var user = factory.CreateAuthenticatedClient(Roles.User);

        using var response = await user.GetAsync($"{Orders}/my?pageSize=0", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- Hết hạn đơn ----------

    [Fact]
    public async Task Expire_DueOrder_ReturnsStockAndMarksExpired()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var user = factory.CreateAuthenticatedClient(Roles.User);
        var (eventId, ticketId) = await CreateEventAsync(admin, total: 10, maxPerOrder: 4, publish: true);
        var orderId = await PlaceOrderAsync(user, (ticketId, 2));
        await MakeExpiredAsync(orderId);

        await SweepAsync();

        Assert.Equal(OrderStatus.Expired, await StatusAsync(orderId));
        Assert.Equal(10, await AvailableAsync(admin, eventId, ticketId));
    }

    [Fact]
    public async Task Expire_OrderNotDue_IsLeftAlone()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var user = factory.CreateAuthenticatedClient(Roles.User);
        var (eventId, ticketId) = await CreateEventAsync(admin, total: 10, maxPerOrder: 4, publish: true);
        var orderId = await PlaceOrderAsync(user, (ticketId, 2));

        await SweepAsync();

        Assert.Equal(OrderStatus.Pending, await StatusAsync(orderId));
        Assert.Equal(8, await AvailableAsync(admin, eventId, ticketId));
    }

    [Fact]
    public async Task Expire_SweptTwice_ReturnsStockOnlyOnce()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var user = factory.CreateAuthenticatedClient(Roles.User);
        var (eventId, ticketId) = await CreateEventAsync(admin, total: 10, maxPerOrder: 4, publish: true);
        var orderId = await PlaceOrderAsync(user, (ticketId, 2));
        await MakeExpiredAsync(orderId);

        await SweepAsync();
        await SweepAsync();

        Assert.Equal(10, await AvailableAsync(admin, eventId, ticketId));
    }

    [Fact]
    public async Task Cancel_AfterExpired_Returns409AndKeepsStock()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var user = factory.CreateAuthenticatedClient(Roles.User);
        var (eventId, ticketId) = await CreateEventAsync(admin, total: 10, maxPerOrder: 4, publish: true);
        var orderId = await PlaceOrderAsync(user, (ticketId, 2));
        await MakeExpiredAsync(orderId);
        await SweepAsync();

        using var response = await user.PostAsync($"{Orders}/{orderId}/cancel", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Orders.NotCancellable", await CodeAsync(response));
        Assert.Equal(10, await AvailableAsync(admin, eventId, ticketId));
    }

    // ---------- Chống bán vượt (Ticket 3) ----------

    [Fact]
    public async Task PlaceOrder_ConcurrentBuyers_NeverOversell()
    {
        // 50 người, mỗi người mua 1 vé, cùng lúc tranh 10 vé: phải đúng 10 đơn thành công, 40 bị từ chối.
        const int total = 10;
        const int buyers = 50;
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var (eventId, ticketId) = await CreateEventAsync(admin, total, maxPerOrder: 1, publish: true);
        var clients = Enumerable.Range(0, buyers).Select(_ => factory.CreateAuthenticatedClient(Roles.User)).ToList();

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var responses = await Task.WhenAll(clients.Select(c => c.PostAsJsonAsync(Orders, Lines((ticketId, 1)), Ct)));
            stopwatch.Stop();

            var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
            var soldOut = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);
            var failures = new List<string>();
            foreach (var response in responses)
            {
                if (response.StatusCode != HttpStatusCode.Created)
                {
                    failures.Add($"{(int)response.StatusCode} {await CodeAsync(response)}");
                }

                response.Dispose();
            }

            var sold = await SoldAsync(ticketId);
            var available = await AvailableAsync(admin, eventId, ticketId);

            // Xem bằng: dotnet test ... --output Detailed (số liệu so sánh 3 cách nằm trong docs/notes.md, Ticket 3).
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"[oversell] {stopwatch.ElapsedMilliseconds} ms | created={created} sold={sold} available={available} | "
                + string.Join(", ", failures.GroupBy(f => f).Select(g => $"{g.Key} x{g.Count()}")));

            // So cả bộ số một lần để khi đỏ thấy đủ: bao nhiêu đơn tạo được, bao nhiêu vé bán ra, kho còn bao nhiêu.
            Assert.Equal(
                (Created: total, SoldOut: buyers - total, Sold: total, Available: 0),
                (Created: created, SoldOut: soldOut, Sold: sold, Available: available));
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    // ---------- Hàm phụ ----------

    // Tổng số vé đã nằm trong đơn của một loại vé, đếm thẳng từ order_items (không tin cột available_quantity).
    private async Task<int> SoldAsync(Guid ticketTypeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Set<OrderItem>().Where(i => i.TicketTypeId == ticketTypeId).SumAsync(i => i.Quantity, Ct);
    }

    // Đưa hạn giữ chỗ về quá khứ để job coi là đã hết hạn (không chờ 10 phút thật).
    private async Task MakeExpiredAsync(Guid orderId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Set<Order>()
            .Where(o => o.Id == orderId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct);
    }

    private async Task SweepAsync()
    {
        using var scope = factory.Services.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var result = await sender.Send(new ExpireDueOrdersCommand(BatchSize: 100), Ct);
        Assert.True(result.IsSuccess);
    }

    private async Task<OrderStatus> StatusAsync(Guid orderId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Set<Order>().AsNoTracking().Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync(Ct);
    }

    private static async Task<Guid> PlaceOrderAsync(HttpClient user, params (Guid TicketTypeId, int Quantity)[] lines)
    {
        using var response = await user.PostAsJsonAsync(Orders, Lines(lines), Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedResponse>(Ct))!.Id;
    }

    private static object Lines(params (Guid TicketTypeId, int Quantity)[] lines) => new
    {
        lines = lines.Select(l => new { ticketTypeId = l.TicketTypeId, quantity = l.Quantity }).ToArray()
    };

    // Sự kiện mở bán ngay (từ 1 giờ trước đến 10 ngày sau), có một loại vé VIP.
    private static async Task<(Guid EventId, Guid TicketTypeId)> CreateEventAsync(
        HttpClient admin, int total, int maxPerOrder, bool publish)
    {
        var now = DateTimeOffset.UtcNow;
        using var created = await admin.PostAsJsonAsync(Events, new CreateEventRequest(
            Name: $"Concert {Guid.NewGuid():N}",
            Description: null,
            Venue: "Nhà hát",
            StartsAt: now.AddDays(11),
            EndsAt: now.AddDays(11).AddHours(3),
            SaleStartsAt: now.AddHours(-1),
            SaleEndsAt: now.AddDays(10)), Ct);
        created.EnsureSuccessStatusCode();
        var eventId = (await created.Content.ReadFromJsonAsync<CreatedResponse>(Ct))!.Id;

        using var ticket = await admin.PostAsJsonAsync($"{Events}/{eventId}/ticket-types",
            new AddTicketTypeRequest("VIP", null, 500_000m, total, maxPerOrder), Ct);
        ticket.EnsureSuccessStatusCode();

        if (publish)
        {
            using var published = await admin.PostAsync($"{Events}/{eventId}/publish", null, Ct);
            published.EnsureSuccessStatusCode();
        }

        var detail = await admin.GetFromJsonAsync<EventDetailResponse>($"{Events}/{eventId}", Ct);
        return (eventId, detail!.TicketTypes[0].Id);
    }

    private static async Task<int> AvailableAsync(HttpClient admin, Guid eventId, Guid ticketTypeId)
    {
        var detail = await admin.GetFromJsonAsync<EventDetailResponse>($"{Events}/{eventId}", Ct);
        return detail!.TicketTypes.Single(t => t.Id == ticketTypeId).AvailableQuantity;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
