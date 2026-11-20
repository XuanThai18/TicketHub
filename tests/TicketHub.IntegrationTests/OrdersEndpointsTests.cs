using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TicketHub.Api.Contracts.Common;
using TicketHub.Api.Contracts.Events;
using TicketHub.Application.Events;
using TicketHub.Domain.Common;

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

    // ---------- Hàm phụ ----------

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
