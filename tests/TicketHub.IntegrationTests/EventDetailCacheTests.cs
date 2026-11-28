using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketHub.Api.Contracts.Common;
using TicketHub.Api.Contracts.Events;
using TicketHub.Application.Events;
using TicketHub.Domain.Common;
using TicketHub.Domain.Events;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.IntegrationTests;

/// <summary>
/// Cache chi tiết sự kiện (Ticket 6.2): phần ít đổi được cache, số vé còn luôn đọc từ DB, bản nháp không bao giờ lộ ra công khai.
/// </summary>
public sealed class EventDetailCacheTests(ApiFactory factory)
{
    private const string Events = "/api/v1/events";
    private const string Orders = "/api/v1/orders";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PublicDetail_IsServedFromCache_EvenIfDatabaseChanged()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var (eventId, _) = await CreatePublishedEventAsync(admin, totalQuantity: 10);
        using var anonymous = factory.CreateClient();
        var first = await anonymous.GetFromJsonAsync<EventDetailResponse>($"{Events}/{eventId}", Ct);

        // Đổi tên thẳng trong DB (không qua API, nên không xoá cache): lần đọc sau vẫn phải ra tên cũ từ cache.
        await RenameInDatabaseAsync(eventId, "Tên mới chỉ có trong DB");

        var second = await anonymous.GetFromJsonAsync<EventDetailResponse>($"{Events}/{eventId}", Ct);
        Assert.Equal(first!.Name, second!.Name);
    }

    [Fact]
    public async Task AvailableQuantity_IsFresh_EvenWhenDetailIsCached()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var (eventId, ticketTypeId) = await CreatePublishedEventAsync(admin, totalQuantity: 10);
        using var anonymous = factory.CreateClient();
        await anonymous.GetFromJsonAsync<EventDetailResponse>($"{Events}/{eventId}", Ct); // làm nóng cache

        using var buyer = factory.CreateAuthenticatedClient(Roles.User);
        using var placed = await buyer.PostAsJsonAsync(Orders, new { lines = new[] { new { ticketTypeId, quantity = 3 } } }, Ct);
        placed.EnsureSuccessStatusCode();

        var detail = await anonymous.GetFromJsonAsync<EventDetailResponse>($"{Events}/{eventId}", Ct);
        Assert.Equal(7, detail!.TicketTypes.Single().AvailableQuantity); // không được còn 10
    }

    [Fact]
    public async Task Draft_IsHiddenFromAnonymous_ButVisibleToAdmin()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var eventId = await CreateDraftEventAsync(admin);
        using var anonymous = factory.CreateClient();

        using var hidden = await anonymous.GetAsync($"{Events}/{eventId}", Ct);
        var asAdmin = await admin.GetFromJsonAsync<EventDetailResponse>($"{Events}/{eventId}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal("Draft", asAdmin!.Status);
    }

    // ---------- Helpers ----------

    private static async Task<Guid> CreateDraftEventAsync(HttpClient admin)
    {
        var now = DateTimeOffset.UtcNow;
        using var created = await admin.PostAsJsonAsync(Events, new CreateEventRequest(
            $"Concert {Guid.NewGuid():N}", null, "Nhà hát",
            now.AddDays(11), now.AddDays(11).AddHours(3), now.AddHours(-1), now.AddDays(10)), Ct);
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<CreatedResponse>(Ct))!.Id;
    }

    private static async Task<(Guid EventId, Guid TicketTypeId)> CreatePublishedEventAsync(HttpClient admin, int totalQuantity)
    {
        var eventId = await CreateDraftEventAsync(admin);
        (await admin.PostAsJsonAsync($"{Events}/{eventId}/ticket-types",
            new AddTicketTypeRequest("VIP", null, 500_000m, totalQuantity, 4), Ct)).EnsureSuccessStatusCode().Dispose();
        (await admin.PostAsync($"{Events}/{eventId}/publish", null, Ct)).EnsureSuccessStatusCode().Dispose();
        var detail = await admin.GetFromJsonAsync<EventDetailResponse>($"{Events}/{eventId}", Ct);
        return (eventId, detail!.TicketTypes[0].Id);
    }

    private async Task RenameInDatabaseAsync(Guid eventId, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Set<Event>().Where(e => e.Id == eventId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Name, name), Ct);
    }
}
