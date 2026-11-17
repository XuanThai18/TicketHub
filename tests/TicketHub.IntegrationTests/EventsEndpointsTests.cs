using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TicketHub.Api.Common;
using TicketHub.Api.Contracts.Events;
using TicketHub.Application.Events;
using TicketHub.Domain.Common;

namespace TicketHub.IntegrationTests;

public sealed class EventsEndpointsTests(ApiFactory factory)
{
    private const string Events = "/api/v1/events";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- Phân quyền ----------

    [Fact]
    public async Task Create_WithoutToken_Returns401()
    {
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.PostAsJsonAsync(Events, NewEvent(), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_AsRegularUser_Returns403()
    {
        using var user = factory.CreateAuthenticatedClient(Roles.User);

        using var response = await user.PostAsJsonAsync(Events, NewEvent(), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- Tạo và xem ----------

    [Fact]
    public async Task Create_AsAdmin_Returns201WithLocation()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);

        using var response = await admin.PostAsJsonAsync(Events, NewEvent(), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>(Ct);
        Assert.Equal($"{Events}/{created!.Id}", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Draft_IsVisibleToAdminButNotToAnonymous()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var anonymous = factory.CreateClient();
        var id = await CreateEventAsync(admin);

        using var asAdmin = await admin.GetAsync($"{Events}/{id}", Ct);
        using var asAnonymous = await anonymous.GetAsync($"{Events}/{id}", Ct);

        Assert.Equal(HttpStatusCode.OK, asAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, asAnonymous.StatusCode);
    }

    [Fact]
    public async Task Published_IsVisibleToAnonymous_WithTicketTypesOrderedByPrice()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var anonymous = factory.CreateClient();
        var id = await CreateEventAsync(admin);
        await AddTicketTypeAsync(admin, id, "VIP", 500_000m);
        await AddTicketTypeAsync(admin, id, "Thường", 100_000m);
        (await admin.PostAsync($"{Events}/{id}/publish", null, Ct)).Dispose();

        var ev = await anonymous.GetFromJsonAsync<EventDetailResponse>($"{Events}/{id}", Ct);

        Assert.Equal("Published", ev!.Status);
        Assert.Equal(["Thường", "VIP"], ev.TicketTypes.Select(t => t.Name));
    }

    // ---------- Luật nghiệp vụ đi hết đường từ HTTP xuống DB ----------

    [Fact]
    public async Task Publish_WithoutTicketTypes_Returns400NoTicketTypes()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var id = await CreateEventAsync(admin);

        using var response = await admin.PostAsync($"{Events}/{id}/publish", null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Events.NoTicketTypes", await CodeAsync(response));
    }

    [Fact]
    public async Task AddTicketType_SameNameDifferentCase_Returns409()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var id = await CreateEventAsync(admin);
        await AddTicketTypeAsync(admin, id, "VIP", 500_000m);

        using var response = await admin.PostAsJsonAsync($"{Events}/{id}/ticket-types",
            new AddTicketTypeRequest(" vip ", null, 1m, 1, 1), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("TicketTypes.DuplicateName", await CodeAsync(response));
    }

    [Fact]
    public async Task Update_AfterPublish_Returns409NotEditable()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var id = await CreateEventAsync(admin);
        await AddTicketTypeAsync(admin, id, "VIP", 500_000m);
        (await admin.PostAsync($"{Events}/{id}/publish", null, Ct)).Dispose();

        var e = NewEvent();
        using var response = await admin.PutAsJsonAsync($"{Events}/{id}",
            new UpdateEventRequest("Tên mới", e.Description, e.Venue, e.StartsAt, e.EndsAt, e.SaleStartsAt, e.SaleEndsAt), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Events.NotEditable", await CodeAsync(response));
    }

    [Fact]
    public async Task Create_InvalidSchedule_Returns400()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        var e = NewEvent();

        using var response = await admin.PostAsJsonAsync(Events, e with { SaleEndsAt = e.EndsAt.AddDays(1) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Events.InvalidSchedule", await CodeAsync(response));
    }

    // ---------- Hàm phụ ----------

    private static CreateEventRequest NewEvent()
    {
        var saleStart = DateTimeOffset.UtcNow.AddDays(1);
        return new CreateEventRequest(
            Name: $"Concert {Guid.NewGuid():N}",
            Description: null,
            Venue: "Nhà hát",
            StartsAt: saleStart.AddDays(11),
            EndsAt: saleStart.AddDays(11).AddHours(3),
            SaleStartsAt: saleStart,
            SaleEndsAt: saleStart.AddDays(10));
    }

    private static async Task<Guid> CreateEventAsync(HttpClient admin)
    {
        using var response = await admin.PostAsJsonAsync(Events, NewEvent(), Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedResponse>(Ct))!.Id;
    }

    private static async Task AddTicketTypeAsync(HttpClient admin, Guid eventId, string name, decimal price)
    {
        using var response = await admin.PostAsJsonAsync($"{Events}/{eventId}/ticket-types",
            new AddTicketTypeRequest(name, null, price, 100, 4), Ct);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
