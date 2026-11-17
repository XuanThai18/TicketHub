using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TicketHub.Api.Contracts.Common;
using TicketHub.Api.Contracts.Events;
using TicketHub.Application.Common.Paging;
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

    // ---------- Danh sách ----------

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    public async Task List_InvalidPaging_Returns400(string queryString)
    {
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.GetAsync($"{Events}?{queryString}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task List_SearchIsCaseInsensitive_AndHidesDrafts()
    {
        // DB dùng chung giữa các test: gắn mã riêng vào tên để chỉ lọc ra sự kiện của test này.
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var anonymous = factory.CreateClient();
        var tag = Guid.NewGuid().ToString("N");
        var published = await CreatePublishedEventAsync(admin, $"Rock {tag}");
        await CreateEventAsync(admin, NewEvent($"Rock {tag} nháp"));

        var page = await anonymous.GetFromJsonAsync<PagedResult<ListEventsResponse>>(
            $"{Events}?search=ROCK%20{tag.ToUpperInvariant()}", Ct);

        var item = Assert.Single(page!.Items);
        Assert.Equal(published, item.Id);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task List_PagesInStartOrder()
    {
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var anonymous = factory.CreateClient();
        var tag = Guid.NewGuid().ToString("N");
        // Tạo lệch thứ tự ngày diễn ra để chắc kết quả được sắp theo ngày, không theo thứ tự tạo.
        var third = await CreatePublishedEventAsync(admin, $"C {tag}", startInDays: 13);
        var first = await CreatePublishedEventAsync(admin, $"A {tag}", startInDays: 11);
        var second = await CreatePublishedEventAsync(admin, $"B {tag}", startInDays: 12);

        var page1 = await anonymous.GetFromJsonAsync<PagedResult<ListEventsResponse>>(
            $"{Events}?search={tag}&page=1&pageSize=2", Ct);
        var page2 = await anonymous.GetFromJsonAsync<PagedResult<ListEventsResponse>>(
            $"{Events}?search={tag}&page=2&pageSize=2", Ct);

        Assert.Equal([first, second], page1!.Items.Select(e => e.Id));
        Assert.Equal([third], page2!.Items.Select(e => e.Id));
        Assert.Equal(3, page1.TotalCount);
        Assert.Equal(2, page1.TotalPages);
    }

    // ---------- Hàm phụ ----------

    private static CreateEventRequest NewEvent(string? name = null, int startInDays = 11)
    {
        var saleStart = DateTimeOffset.UtcNow.AddDays(1);
        return new CreateEventRequest(
            Name: name ?? $"Concert {Guid.NewGuid():N}",
            Description: null,
            Venue: "Nhà hát",
            StartsAt: saleStart.AddDays(startInDays),
            EndsAt: saleStart.AddDays(startInDays).AddHours(3),
            SaleStartsAt: saleStart,
            SaleEndsAt: saleStart.AddDays(10));
    }

    private static async Task<Guid> CreateEventAsync(HttpClient admin, CreateEventRequest? request = null)
    {
        using var response = await admin.PostAsJsonAsync(Events, request ?? NewEvent(), Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedResponse>(Ct))!.Id;
    }

    // Publish cần ít nhất một loại vé nên thêm sẵn một loại.
    private static async Task<Guid> CreatePublishedEventAsync(HttpClient admin, string name, int startInDays = 11)
    {
        var id = await CreateEventAsync(admin, NewEvent(name, startInDays));
        await AddTicketTypeAsync(admin, id, "Thường", 100_000m);
        using var response = await admin.PostAsync($"{Events}/{id}/publish", null, Ct);
        response.EnsureSuccessStatusCode();
        return id;
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
