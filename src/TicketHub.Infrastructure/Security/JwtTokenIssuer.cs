using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace TicketHub.Infrastructure.Security;

/// <summary>Ký JWT bằng cùng khoá mà API dùng để kiểm (đăng nhập, /dev/token, test).</summary>
public sealed class JwtTokenIssuer(IOptions<JwtOptions> options, TimeProvider timeProvider)
{
    public const string RoleClaim = "role";

    public string Issue(string subject, IEnumerable<string> roles, TimeSpan lifetime) =>
        Issue(subject, roles, lifetime, []).Token;

    public (string Token, DateTimeOffset ExpiresAt) Issue(
        string subject, IEnumerable<string> roles, TimeSpan lifetime, IEnumerable<Claim> extraClaims)
    {
        var jwt = options.Value;
        var now = timeProvider.GetUtcNow();
        var expiresAt = now.Add(lifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        claims.AddRange(roles.Select(role => new Claim(RoleClaim, role)));
        claims.AddRange(extraClaims);

        var token = new JwtSecurityToken(
            issuer: jwt.Issuer,
            audience: jwt.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)), SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
