using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketHub.Api.Contracts.Common;
using TicketHub.Api.Contracts.Events;
using TicketHub.Application.Common.Paging;
using TicketHub.Application.Events;
using TicketHub.Domain.Common;
using TicketHub.Domain.Events;
using TicketHub.Infrastructure.Persistence.Contexts;

namespace TicketHub.IntegrationTests;

/// <summary>Cache danh sách sự kiện (Ticket 6.3): kết quả tìm kiếm được lưu theo tham số, không lẫn giữa các tham số.</summary>
public sealed class ListEventsCacheTests(ApiFactory factory)
{
    private const string Events = "/api/v1/events";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListResult_IsServedFromCache_EvenIfDatabaseChanged()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var search = $"Cache{Guid.NewGuid():N}";
        var eventId = await CreatePublishedEventAsync(admin, search);
        using var anonymous = factory.CreateClient();
        var url = $"{Events}?search={search}";

        var first = await anonymous.GetFromJsonAsync<PagedResult<ListEventsResponse>>(url, Ct);

        await RenameInDatabaseAsync(eventId, search + " đã đổi");

        var second = await anonymous.GetFromJsonAsync<PagedResult<ListEventsResponse>>(url, Ct);
        Assert.Equal(first!.Items.Single().Name, second!.Items.Single().Name);
    }

    [Fact]
    public async Task DifferentParameters_DoNotShareCachedResults()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var search = $"Cache{Guid.NewGuid():N}";
        var eventId = await CreatePublishedEventAsync(admin, search);
        using var anonymous = factory.CreateClient();

        var matching = await anonymous.GetFromJsonAsync<PagedResult<ListEventsResponse>>($"{Events}?search={search}", Ct);
        var renamed = search + " khac";
        await RenameInDatabaseAsync(eventId, renamed);

        // Tham số khác (tìm tên mới) phải đọc DB mới, không dùng lại kết quả đã cache cho tham số trước.
        var other = await anonymous.GetFromJsonAsync<PagedResult<ListEventsResponse>>(
            $"{Events}?search={Uri.EscapeDataString(renamed)}", Ct);

        Assert.Single(matching!.Items);
        Assert.Single(other!.Items);
        Assert.Equal(eventId, other.Items.Single().Id);
    }

    // ---------- Helpers ----------

    private static async Task<Guid> CreatePublishedEventAsync(HttpClient admin, string name)
    {
        var now = DateTimeOffset.UtcNow;
        using var created = await admin.PostAsJsonAsync(Events, new CreateEventRequest(
            name, null, "Nhà hát",
            now.AddDays(11), now.AddDays(11).AddHours(3), now.AddHours(-1), now.AddDays(10)), Ct);
        created.EnsureSuccessStatusCode();
        var eventId = (await created.Content.ReadFromJsonAsync<CreatedResponse>(Ct))!.Id;
        (await admin.PostAsJsonAsync($"{Events}/{eventId}/ticket-types",
            new AddTicketTypeRequest("VIP", null, 500_000m, 10, 4), Ct)).EnsureSuccessStatusCode().Dispose();
        (await admin.PostAsync($"{Events}/{eventId}/publish", null, Ct)).EnsureSuccessStatusCode().Dispose();
        return eventId;
    }

    private async Task RenameInDatabaseAsync(Guid eventId, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Set<Event>().Where(e => e.Id == eventId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Name, name), Ct);
    }
}
