using System.Net.Http.Json;
using TicketHub.Api.Contracts.Common;
using TicketHub.Api.Contracts.Events;
using TicketHub.Application.Common.Paging;
using TicketHub.Application.Events;
using TicketHub.Domain.Common;

namespace TicketHub.IntegrationTests;

/// <summary>Ghi xong phải thấy ngay ở cache công khai (Ticket 6.4): mỗi thao tác ghi xoá đúng cache nó làm lệch.</summary>
public sealed class EventWriteInvalidationTests(ApiFactory factory)
{
    private const string Events = "/api/v1/events";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PublishEvent_IsVisibleAtOnce_InList()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var term = $"Pub{Guid.NewGuid():N}";
        var eventId = await CreateDraftEventAsync(admin, term);
        await AddTicketTypeAsync(admin, eventId);
        using var anonymous = factory.CreateClient();
        var url = $"{Events}?search={term}";
        var before = await anonymous.GetFromJsonAsync<PagedResult<ListEventsResponse>>(url, Ct); // làm nóng cache: chưa có
        Assert.Empty(before!.Items);

        (await admin.PostAsync($"{Events}/{eventId}/publish", null, Ct)).EnsureSuccessStatusCode().Dispose();

        var after = await anonymous.GetFromJsonAsync<PagedResult<ListEventsResponse>>(url, Ct);
        Assert.Equal(eventId, after!.Items.Single().Id);
    }

    [Fact]
    public async Task CancelEvent_IsVisibleAtOnce_InDetailAndList()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var term = $"Can{Guid.NewGuid():N}";
        var eventId = await CreatePublishedEventAsync(admin, term);
        using var anonymous = factory.CreateClient();
        var url = $"{Events}?search={term}";
        await anonymous.GetFromJsonAsync<EventDetailResponse>($"{Events}/{eventId}", Ct); // làm nóng cache
        await anonymous.GetFromJsonAsync<PagedResult<ListEventsResponse>>(url, Ct);

        (await admin.PostAsync($"{Events}/{eventId}/cancel", null, Ct)).EnsureSuccessStatusCode().Dispose();

        var detail = await anonymous.GetFromJsonAsync<EventDetailResponse>($"{Events}/{eventId}", Ct);
        var page = await anonymous.GetFromJsonAsync<PagedResult<ListEventsResponse>>(url, Ct);
        Assert.Equal("Cancelled", detail!.Status);
        Assert.Equal("Cancelled", page!.Items.Single().Status);
    }

    [Fact]
    public async Task UpdateTicketType_IsVisibleAtOnce_InPublicDetail()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var eventId = await CreatePublishedEventAsync(admin, $"Ticket {Guid.NewGuid():N}");
        using var anonymous = factory.CreateClient();
        var first = await anonymous.GetFromJsonAsync<EventDetailResponse>($"{Events}/{eventId}", Ct); // làm nóng cache
        var ticketTypeId = first!.TicketTypes.Single().Id;

        using (var updated = await admin.PutAsJsonAsync($"{Events}/{eventId}/ticket-types/{ticketTypeId}",
                   new UpdateTicketTypeRequest("VIP", null, 750_000m, 10, 4), Ct))
        {
            updated.EnsureSuccessStatusCode();
        }

        var detail = await anonymous.GetFromJsonAsync<EventDetailResponse>($"{Events}/{eventId}", Ct);
        Assert.Equal(750_000m, detail!.TicketTypes.Single().Price);
    }

    // ---------- Helpers ----------

    private static async Task<Guid> CreateDraftEventAsync(HttpClient admin, string name)
    {
        var now = DateTimeOffset.UtcNow;
        using var created = await admin.PostAsJsonAsync(Events, new CreateEventRequest(
            name, null, "Nhà hát",
            now.AddDays(11), now.AddDays(11).AddHours(3), now.AddHours(-1), now.AddDays(10)), Ct);
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<CreatedResponse>(Ct))!.Id;
    }

    private static async Task AddTicketTypeAsync(HttpClient admin, Guid eventId)
    {
        (await admin.PostAsJsonAsync($"{Events}/{eventId}/ticket-types",
            new AddTicketTypeRequest("VIP", null, 500_000m, 10, 4), Ct)).EnsureSuccessStatusCode().Dispose();
    }

    private static async Task<Guid> CreatePublishedEventAsync(HttpClient admin, string name)
    {
        var eventId = await CreateDraftEventAsync(admin, name);
        await AddTicketTypeAsync(admin, eventId);
        (await admin.PostAsync($"{Events}/{eventId}/publish", null, Ct)).EnsureSuccessStatusCode().Dispose();
        return eventId;
    }

}
