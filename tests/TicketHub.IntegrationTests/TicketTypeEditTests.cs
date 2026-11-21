using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketHub.Api.Contracts.Common;
using TicketHub.Api.Contracts.Events;
using TicketHub.Application.Common.Exceptions;
using TicketHub.Application.Events;
using TicketHub.Domain.Common;
using TicketHub.Domain.OrderItems;
using TicketHub.Domain.TicketTypes;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.IntegrationTests;

/// <summary>Sửa loại vé khi sự kiện đang bán: PUT /events/{id}/ticket-types/{ticketTypeId}, chống ghi đè bằng xmin.</summary>
public sealed class TicketTypeEditTests(ApiFactory factory)
{
    private const string Events = "/api/v1/events";
    private const string Orders = "/api/v1/orders";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- xmin bắt được người mua chen vào ----------

    [Fact]
    public async Task SaveAfterConcurrentPurchase_ThrowsConflictInsteadOfOverwritingStock()
    {
        // Chạy theo thứ tự cố định để không phụ thuộc may rủi:
        // admin đọc loại vé → một người mua 1 vé (UPDATE nguyên tử, xmin đổi) → admin lưu bản đã đọc.
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var buyer = factory.CreateAuthenticatedClient(Roles.User);
        var (_, ticketId) = await CreateEventAsync(admin, total: 10);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ticket = await db.Set<TicketType>().SingleAsync(t => t.Id == ticketId, Ct); // available = 10

        using (var bought = await buyer.PostAsJsonAsync(Orders, Lines(ticketId, 1), Ct))
        {
            Assert.Equal(HttpStatusCode.Created, bought.StatusCode); // DB: available = 9
        }

        // Bản admin đang giữ vẫn thấy 10 vé còn, 0 vé đã bán → sẽ tính available = 20 − 0 = 20 (sai, đúng phải là 19).
        Assert.True(ticket.Update("VIP", null, 500_000m, totalQuantity: 20, maxQuantityPerOrder: 4).IsSuccess);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => db.SaveChangesAsync(Ct));
        Assert.Equal((Total: 10, Available: 9), await StockAsync(ticketId)); // vé vừa bán không bị ghi đè
    }

    // ---------- Đua thật qua HTTP ----------

    [Fact]
    public async Task RaiseTotalWhileBuyersRace_KeepsTotalEqualToAvailablePlusSold()
    {
        // Đợt 1: 50 người tranh 10 vé, đúng lúc đó admin tăng tổng lên 20.
        // Đợt 2 (sau khi admin lưu xong): 20 người nữa. Tổng cầu 70 > 20 nên phải bán đúng 20, kho về 0.
        // Nếu lần lưu của admin ghi đè số vé vừa bán (lost update), số bán ra sẽ vượt 20 hoặc kho lệch.
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var (eventId, ticketId) = await CreateEventAsync(admin, total: 10, maxPerOrder: 1);
        var clients = Enumerable.Range(0, 70).Select(_ => factory.CreateAuthenticatedClient(Roles.User)).ToList();

        try
        {
            var firstWave = Task.WhenAll(clients.Take(50).Select(c => c.PostAsJsonAsync(Orders, Lines(ticketId, 1), Ct)));
            var adminConflicts = await RaiseTotalAsync(admin, eventId, ticketId, newTotal: 20);
            var responses = (await firstWave).ToList();
            var soldInFirstWave = await SoldAsync(ticketId);

            responses.AddRange(await Task.WhenAll(clients.Skip(50).Select(c => c.PostAsJsonAsync(Orders, Lines(ticketId, 1), Ct))));
            var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
            foreach (var response in responses)
            {
                response.Dispose();
            }

            var (total, available) = await StockAsync(ticketId);
            var sold = await SoldAsync(ticketId);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"[edit-race] firstWaveSold={soldInFirstWave} total={total} available={available} sold={sold} created={created} adminConflicts={adminConflicts}");

            Assert.Equal((Total: 20, Available: 0, Sold: 20, Created: 20), (Total: total, Available: available, Sold: sold, Created: created));
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    // ---------- Luồng HTTP ----------

    [Fact]
    public async Task Update_PublishedEvent_Returns204AndAppliesNewValues()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var buyer = factory.CreateAuthenticatedClient(Roles.User);
        var (eventId, ticketId) = await CreateEventAsync(admin, total: 10);
        (await buyer.PostAsJsonAsync(Orders, Lines(ticketId, 3), Ct)).Dispose(); // đã bán 3

        using var response = await admin.PutAsJsonAsync(TicketTypeUrl(eventId, ticketId),
            new UpdateTicketTypeRequest("VIP Gold", "Hàng đầu", 650_000m, TotalQuantity: 30, MaxQuantityPerOrder: 6), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var ticket = (await admin.GetFromJsonAsync<EventDetailResponse>($"{Events}/{eventId}", Ct))!.TicketTypes.Single();
        Assert.Equal(("VIP Gold", 650_000m, 27, 6), (ticket.Name, ticket.Price, ticket.AvailableQuantity, ticket.MaxQuantityPerOrder));
    }

    [Fact]
    public async Task Update_TotalBelowSold_Returns409()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var buyer = factory.CreateAuthenticatedClient(Roles.User);
        var (eventId, ticketId) = await CreateEventAsync(admin, total: 10);
        (await buyer.PostAsJsonAsync(Orders, Lines(ticketId, 3), Ct)).Dispose(); // đã bán 3

        using var response = await admin.PutAsJsonAsync(TicketTypeUrl(eventId, ticketId), Request(total: 2), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("TicketTypes.TotalBelowSold", await CodeAsync(response));
        Assert.Equal((Total: 10, Available: 7), await StockAsync(ticketId));
    }

    [Fact]
    public async Task Update_CancelledEvent_Returns409()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var (eventId, ticketId) = await CreateEventAsync(admin, total: 10);
        (await admin.PostAsync($"{Events}/{eventId}/cancel", null, Ct)).EnsureSuccessStatusCode().Dispose();

        using var response = await admin.PutAsJsonAsync(TicketTypeUrl(eventId, ticketId), Request(total: 20), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("TicketTypes.EventCancelled", await CodeAsync(response));
    }

    [Fact]
    public async Task Update_TicketTypeOfAnotherEvent_Returns404()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var (_, ticketId) = await CreateEventAsync(admin, total: 10);
        var (otherEventId, _) = await CreateEventAsync(admin, total: 10);

        using var response = await admin.PutAsJsonAsync(TicketTypeUrl(otherEventId, ticketId), Request(total: 20), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("TicketTypes.NotFound", await CodeAsync(response));
    }

    [Fact]
    public async Task Update_AsRegularUser_Returns403()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var user = factory.CreateAuthenticatedClient(Roles.User);
        var (eventId, ticketId) = await CreateEventAsync(admin, total: 10);

        using var response = await user.PutAsJsonAsync(TicketTypeUrl(eventId, ticketId), Request(total: 20), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- Hàm phụ ----------

    private static string TicketTypeUrl(Guid eventId, Guid ticketTypeId) => $"{Events}/{eventId}/ticket-types/{ticketTypeId}";

    private static UpdateTicketTypeRequest Request(int total) =>
        new("VIP", null, 500_000m, TotalQuantity: total, MaxQuantityPerOrder: 1);

    private static object Lines(Guid ticketTypeId, int quantity) => new
    {
        lines = new[] { new { ticketTypeId, quantity } }
    };

    // Admin bấm lưu lại khi nhận 409 ConcurrentUpdate (handler đã tự thử 3 lần). Trả số lần nhận 409.
    private static async Task<int> RaiseTotalAsync(HttpClient admin, Guid eventId, Guid ticketId, int newTotal)
    {
        for (var conflicts = 0; conflicts < 10; conflicts++)
        {
            using var response = await admin.PutAsJsonAsync(TicketTypeUrl(eventId, ticketId), Request(newTotal), Ct);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return conflicts;
            }

            Assert.Equal("TicketTypes.ConcurrentUpdate", await CodeAsync(response));
        }

        Assert.Fail("Admin không lưu được sau 10 lần bấm.");
        return -1;
    }

    // Sự kiện đang mở bán với một loại vé VIP.
    private static async Task<(Guid EventId, Guid TicketTypeId)> CreateEventAsync(HttpClient admin, int total, int maxPerOrder = 4)
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

        using var published = await admin.PostAsync($"{Events}/{eventId}/publish", null, Ct);
        published.EnsureSuccessStatusCode();

        var detail = await admin.GetFromJsonAsync<EventDetailResponse>($"{Events}/{eventId}", Ct);
        return (eventId, detail!.TicketTypes[0].Id);
    }

    private async Task<(int Total, int Available)> StockAsync(Guid ticketTypeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stock = await db.Set<TicketType>().AsNoTracking()
            .Where(t => t.Id == ticketTypeId)
            .Select(t => new { t.TotalQuantity, t.AvailableQuantity })
            .SingleAsync(Ct);
        return (stock.TotalQuantity, stock.AvailableQuantity);
    }

    private async Task<int> SoldAsync(Guid ticketTypeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Set<OrderItem>().Where(i => i.TicketTypeId == ticketTypeId).SumAsync(i => i.Quantity, Ct);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
