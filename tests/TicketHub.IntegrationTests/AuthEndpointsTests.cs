using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TicketHub.Api.Contracts.Auth;
using TicketHub.Api.Options;
using TicketHub.Application.Auth;
using TicketHub.Domain.Common;

namespace TicketHub.IntegrationTests;

public sealed class AuthEndpointsTests(ApiFactory factory)
{
    private const string Auth = "/api/v1/auth";
    private const string Password = "Passw0rd!";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- Đăng nhập ----------

    [Fact]
    public async Task Login_ValidCredentials_ReturnsTokensAndMeWorks()
    {
        var email = await CreateUserAsync(Roles.Manager);

        var tokens = await LoginBodyAsync(email, Password);
        using var client = Bearer(tokens.AccessToken);
        var me = await client.GetFromJsonAsync<CurrentUserResponse>($"{Auth}/me", Ct);

        Assert.Equal(email, me!.Email);
        Assert.Equal([Roles.Manager], me.Roles);
        Assert.NotNull(me.LastLoginAt);
    }

    [Fact]
    public async Task Login_WrongPasswordAndUnknownEmail_LookIdentical()
    {
        var email = await CreateUserAsync();
        using var client = MobileClient();

        using var wrongPassword = await client.PostAsJsonAsync($"{Auth}/login", new LoginRequest(email, "Wrong1234"), Ct);
        using var unknownEmail = await client.PostAsJsonAsync($"{Auth}/login", new LoginRequest(NewEmail(), "Wrong1234"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);
        // Không lộ email nào tồn tại.
        Assert.Equal(await CodeAsync(wrongPassword), await CodeAsync(unknownEmail));
    }

    [Fact]
    public async Task Login_FiveWrongPasswords_LocksEvenTheCorrectPassword()
    {
        var email = await CreateUserAsync();
        using var client = MobileClient();

        for (var i = 0; i < 5; i++)
            (await client.PostAsJsonAsync($"{Auth}/login", new LoginRequest(email, "Wrong1234"), Ct)).Dispose();

        using var correct = await client.PostAsJsonAsync($"{Auth}/login", new LoginRequest(email, Password), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, correct.StatusCode);
        Assert.Equal("Auth.AccountLocked", await CodeAsync(correct));
    }

    // ---------- Refresh token (body) ----------

    [Fact]
    public async Task Refresh_RotatesToken_AndOldTokenStopsWorking()
    {
        var email = await CreateUserAsync();
        var first = await LoginBodyAsync(email, Password);

        var second = await RefreshBodyAsync(first.RefreshToken!);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        using var client = MobileClient();
        using var reused = await client.PostAsJsonAsync($"{Auth}/refresh", new RefreshRequest(first.RefreshToken), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
    }

    [Fact]
    public async Task Refresh_ReusingRotatedToken_RevokesWholeSessionFamily()
    {
        var email = await CreateUserAsync();
        var stolen = await LoginBodyAsync(email, Password);
        var legit = await RefreshBodyAsync(stolen.RefreshToken!); // chủ tài khoản đã đổi token

        // Kẻ trộm dùng token cũ → hệ thống phát hiện và cắt cả họ phiên...
        using var client = MobileClient();
        (await client.PostAsJsonAsync($"{Auth}/refresh", new RefreshRequest(stolen.RefreshToken), Ct)).Dispose();

        // ...nên token mới nhất của chủ tài khoản cũng hết hiệu lực, buộc đăng nhập lại.
        using var afterTheft = await client.PostAsJsonAsync($"{Auth}/refresh", new RefreshRequest(legit.RefreshToken), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, afterTheft.StatusCode);
    }

    // ---------- Refresh token (cookie HttpOnly, mặc định cho web) ----------

    [Fact]
    public async Task Login_DefaultTransport_SetsHttpOnlyCookieAndHidesTokenFromBody()
    {
        var email = await CreateUserAsync();
        using var client = factory.CreateCookieClient();

        using var response = await client.PostAsJsonAsync($"{Auth}/login", new LoginRequest(email, Password), Ct);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(Ct);
        Assert.Null(body!.RefreshToken);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/v1/auth", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshWithCookie_RequiresCsrfHeader()
    {
        var email = await CreateUserAsync();
        using var client = factory.CreateCookieClient();
        (await client.PostAsJsonAsync($"{Auth}/login", new LoginRequest(email, Password), Ct)).EnsureSuccessStatusCode();

        using var withoutHeader = await client.PostAsync($"{Auth}/refresh", null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, withoutHeader.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Auth}/refresh");
        request.Headers.Add(RefreshTokenCookieOptions.CsrfHeader, "1");
        using var withHeader = await client.SendAsync(request, Ct);

        withHeader.EnsureSuccessStatusCode();
        Assert.Contains(withHeader.Headers.GetValues("Set-Cookie"), c => c.StartsWith("tickethub_refresh=", StringComparison.Ordinal));
    }

    // ---------- Đăng xuất, đổi mật khẩu ----------

    [Fact]
    public async Task Logout_RevokesRefreshTokenOfThatSession()
    {
        var email = await CreateUserAsync();
        var tokens = await LoginBodyAsync(email, Password);

        using var client = Bearer(tokens.AccessToken);
        using var logout = await client.PostAsync($"{Auth}/logout", null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var refresh = await MobileClient().PostAsJsonAsync($"{Auth}/refresh", new RefreshRequest(tokens.RefreshToken), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_RevokesOtherDevicesButKeepsCurrent()
    {
        var email = await CreateUserAsync();
        var laptop = await LoginBodyAsync(email, Password);
        var phone = await LoginBodyAsync(email, Password);

        using var client = Bearer(laptop.AccessToken);
        using var changed = await client.PostAsJsonAsync($"{Auth}/change-password", new ChangePasswordRequest(Password, "NewPassw0rd!"), Ct);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        using var mobile = MobileClient();
        using var phoneRefresh = await mobile.PostAsJsonAsync($"{Auth}/refresh", new RefreshRequest(phone.RefreshToken), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, phoneRefresh.StatusCode);

        await RefreshBodyAsync(laptop.RefreshToken!); // thiết bị đang đổi mật khẩu vẫn dùng tiếp được

        using var oldPassword = await mobile.PostAsJsonAsync($"{Auth}/login", new LoginRequest(email, Password), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        await LoginBodyAsync(email, "NewPassw0rd!");
    }

    [Fact]
    public async Task Sessions_ListsActiveDevicesAndMarksCurrent()
    {
        var email = await CreateUserAsync();
        await LoginBodyAsync(email, Password);
        var current = await LoginBodyAsync(email, Password);

        using var client = Bearer(current.AccessToken);
        var sessions = await client.GetFromJsonAsync<List<SessionResponse>>($"{Auth}/sessions", Ct);

        Assert.Equal(2, sessions!.Count);
        Assert.Single(sessions, s => s.IsCurrent);
    }

    // ---------- Quản lý tài khoản ----------

    [Fact]
    public async Task CreateUser_RequiresAdmin_AndRejectsDuplicateEmail()
    {
        var request = new CreateUserRequest(NewEmail(), "Bob", Password, [Roles.User]);

        using var asUser = factory.CreateAuthenticatedClient(Roles.User);
        using var forbidden = await asUser.PostAsJsonAsync($"{Auth}/users", request, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var asAdmin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var created = await asAdmin.PostAsJsonAsync($"{Auth}/users", request, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var duplicate = await asAdmin.PostAsJsonAsync($"{Auth}/users", request with { Email = request.Email.ToUpperInvariant() }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Register_IsDisabledByDefault()
    {
        using var response = await MobileClient().PostAsJsonAsync($"{Auth}/register", new RegisterRequest(NewEmail(), "Eve", Password), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Auth.SelfRegistrationDisabled", await CodeAsync(response));
    }

    [Fact]
    public async Task Login_ExceedingSensitiveRateLimit_Returns429()
    {
        // Host riêng với giới hạn thấp (bộ đếm rate limit không dùng chung với host chính).
        await using var strict = factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:SensitivePermitLimit", "3"));
        using var client = strict.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            using var response = await client.PostAsJsonAsync($"{Auth}/login", new LoginRequest(NewEmail(), "Wrong1234"), Ct);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    // ---------- helpers ----------

    private static string NewEmail() => $"u{Guid.NewGuid():N}@test.local";

    private async Task<string> CreateUserAsync(string role = Roles.User)
    {
        var email = NewEmail();
        using var admin = factory.CreateAuthenticatedClient(Roles.Admin);
        using var response = await admin.PostAsJsonAsync($"{Auth}/users", new CreateUserRequest(email, "Test User", Password, [role]), Ct);
        response.EnsureSuccessStatusCode();
        return email;
    }

    /// <summary>Client "mobile": nhận refresh token trong body thay vì cookie.</summary>
    private HttpClient MobileClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(RefreshTokenCookieOptions.ClientTypeHeader, "mobile");
        return client;
    }

    private HttpClient Bearer(string accessToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private async Task<TokenResponse> LoginBodyAsync(string email, string password)
    {
        using var client = MobileClient();
        using var response = await client.PostAsJsonAsync($"{Auth}/login", new LoginRequest(email, password), Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
    }

    private async Task<TokenResponse> RefreshBodyAsync(string refreshToken)
    {
        using var client = MobileClient();
        using var response = await client.PostAsJsonAsync($"{Auth}/refresh", new RefreshRequest(refreshToken), Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Ct))!;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();
}
