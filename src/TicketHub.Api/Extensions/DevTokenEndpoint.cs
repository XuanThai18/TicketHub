using TicketHub.Infrastructure.Security;

namespace TicketHub.Api.Extensions;

public static class DevTokenEndpoint
{
    public sealed record DevTokenRequest(string Subject, string[]? Roles);

    /// <summary>POST /dev/token — CHỈ map ở Development, để thử API trên Scalar/Postman khi chưa có màn đăng nhập.</summary>
    public static WebApplication MapDevTokenEndpoint(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            return app;

        app.MapPost("/dev/token", (DevTokenRequest request, JwtTokenIssuer issuer) =>
                Results.Ok(new { accessToken = issuer.Issue(request.Subject, request.Roles ?? [], TimeSpan.FromHours(8)) }))
            .AllowAnonymous()
            .ExcludeFromDescription();

        return app;
    }
}
