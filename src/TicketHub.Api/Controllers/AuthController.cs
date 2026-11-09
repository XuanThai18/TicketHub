using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TicketHub.Api.Contracts.Auth;
using TicketHub.Api.Extensions;
using TicketHub.Api.Options;
using TicketHub.Application.Auth;
using TicketHub.Domain.Auth;
using TicketHub.Domain.Shared;

namespace TicketHub.Api.Controllers;

[ApiVersion(1.0)]
[Route("api/v{version:apiVersion}/auth")] // chữ thường cố định: đường dẫn cookie phân biệt hoa thường
public sealed class AuthController(ISender sender, IOptions<RefreshTokenCookieOptions> cookieOptions) : BaseApiController
{
    private RefreshTokenCookieOptions Cookie => cookieOptions.Value;

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(SecuritySetup.SensitivePolicy)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new LoginCommand(request.Email, request.Password, ClientIp, UserAgent), cancellationToken);

        return result.IsFailure ? ToProblem(result.Error) : Tokens(result.Value, useCookie: !WantsBodyTransport());
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting(SecuritySetup.SensitivePolicy)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RefreshRequest? request, CancellationToken cancellationToken)
    {
        var token = request?.RefreshToken;
        var fromCookie = string.IsNullOrEmpty(token);
        if (fromCookie)
        {
            if (!HasCsrfHeader())
                return ToProblem(CsrfHeaderMissing);
            token = Request.Cookies[Cookie.CookieName];
        }

        if (string.IsNullOrEmpty(token))
            return ToProblem(AuthErrors.InvalidRefreshToken);

        var result = await sender.Send(new RefreshTokenCommand(token, ClientIp, UserAgent), cancellationToken);
        if (result.IsFailure)
        {
            if (fromCookie)
                DeleteRefreshCookie();
            return ToProblem(result.Error);
        }

        // Trả token mới theo đúng kênh client đã dùng: cookie vào thì cookie ra.
        return Tokens(result.Value, useCookie: fromCookie);
    }

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new LogoutCommand(CurrentUserId, CurrentSessionId), cancellationToken);
        DeleteRefreshCookie();
        return HandleResult(result);
    }

    [HttpPost("logout-all")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> LogoutAll(CancellationToken cancellationToken)
    {
        var result = await sender.Send(new LogoutAllCommand(CurrentUserId), cancellationToken);
        DeleteRefreshCookie();
        return HandleResult(result);
    }

    [HttpGet("me")]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Me(CancellationToken cancellationToken) =>
        HandleResult(await sender.Send(new GetCurrentUserQuery(CurrentUserId), cancellationToken));

    [HttpGet("sessions")]
    [ProducesResponseType<IReadOnlyList<SessionResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Sessions(CancellationToken cancellationToken) =>
        HandleResult(await sender.Send(new ListMySessionsQuery(CurrentUserId, CurrentSessionId), cancellationToken));

    [HttpPost("change-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken) =>
        HandleResult(await sender.Send(
            new ChangePasswordCommand(CurrentUserId, CurrentSessionId, request.CurrentPassword, request.NewPassword),
            cancellationToken));

    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(SecuritySetup.SensitivePolicy)]
    [ProducesResponseType<CreatedUserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken) =>
        Created(await sender.Send(new RegisterCommand(request.Email, request.FullName, request.Password), cancellationToken));

    [HttpPost("users")]
    [Authorize(Policy = Policies.RequireAdminRole)]
    [ProducesResponseType<CreatedUserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateUser(CreateUserRequest request, CancellationToken cancellationToken) =>
        Created(await sender.Send(
            new CreateUserCommand(request.Email, request.FullName, request.Password, request.Roles), cancellationToken));

    private static readonly Error CsrfHeaderMissing =
        Error.Forbidden("Auth.CsrfHeaderMissing", $"Header '{RefreshTokenCookieOptions.CsrfHeader}' is required when using the refresh cookie.");

    private Guid CurrentUserId => Guid.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id) ? id : Guid.Empty;

    private Guid CurrentSessionId => Guid.TryParse(User.FindFirstValue(JwtRegisteredClaimNames.Sid), out var id) ? id : Guid.Empty;

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    private string? UserAgent => Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;

    private bool WantsBodyTransport() =>
        Cookie.DefaultTransport == RefreshTokenTransport.Body
        || string.Equals(Request.Headers[RefreshTokenCookieOptions.ClientTypeHeader], "mobile", StringComparison.OrdinalIgnoreCase);

    private bool HasCsrfHeader() => Request.Headers.ContainsKey(RefreshTokenCookieOptions.CsrfHeader);

    private OkObjectResult Tokens(AuthTokens tokens, bool useCookie)
    {
        if (!useCookie)
            return Ok(new TokenResponse(tokens.AccessToken, tokens.AccessTokenExpiresAt, tokens.RefreshToken, tokens.RefreshTokenExpiresAt));

        Response.Cookies.Append(Cookie.CookieName, tokens.RefreshToken, CookieSettings(tokens.RefreshTokenExpiresAt));
        return Ok(new TokenResponse(tokens.AccessToken, tokens.AccessTokenExpiresAt, null, tokens.RefreshTokenExpiresAt));
    }

    private void DeleteRefreshCookie() => Response.Cookies.Delete(Cookie.CookieName, CookieSettings(expires: null));

    private CookieOptions CookieSettings(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = Cookie.SameSite,
        Path = Cookie.CookiePath,
        Expires = expires,
        IsEssential = true
    };

    private ObjectResult Created(Result<Guid> result) =>
        result.IsFailure ? ToProblem(result.Error) : StatusCode(StatusCodes.Status201Created, new CreatedUserResponse(result.Value));
}
