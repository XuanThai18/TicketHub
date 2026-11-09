using TicketHub.Domain.Auth;

namespace TicketHub.Application.Auth;

public sealed record AuthTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt)
{
    internal static AuthTokens Issue(ITokenService tokens, User user, UserSession session, string refreshToken)
    {
        var access = tokens.CreateAccessToken(user, session.Id);
        return new AuthTokens(access.Token, access.ExpiresAt, refreshToken, session.ExpiresAt);
    }
}
