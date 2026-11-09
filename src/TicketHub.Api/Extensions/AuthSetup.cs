using System.IdentityModel.Tokens.Jwt;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TicketHub.Api.Options;
using TicketHub.Api.Services;
using TicketHub.Application.Abstractions;
using TicketHub.Domain.Common;
using TicketHub.Infrastructure.Security;

namespace TicketHub.Api.Extensions;

public static class Policies
{
    public const string RequireAdminRole = nameof(RequireAdminRole);
    public const string RequireManagerRole = nameof(RequireManagerRole);
}

public static class AuthSetup
{
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services)
    {
        services.AddOptions<JwtOptions>()
            .BindConfiguration(JwtOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((options, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;

                // Giữ nguyên tên claim trong token ("sub", "role"), không map sang URI kiểu WS-Fed.
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
                    ClockSkew = TimeSpan.FromSeconds(jwt.ClockSkewSeconds),
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = JwtTokenIssuer.RoleClaim
                };
            });

        services.AddAuthorizationBuilder()
            // Secure-by-default: endpoint không gắn attribute vẫn phải đăng nhập; mở bằng [AllowAnonymous].
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.RequireAdminRole, policy => policy.RequireRole(Roles.Admin))
            .AddPolicy(Policies.RequireManagerRole, policy => policy.RequireRole(Roles.Admin, Roles.Manager));

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddSingleton<JwtTokenIssuer>();
        services.AddOptions<RefreshTokenCookieOptions>().BindConfiguration(RefreshTokenCookieOptions.SectionName);

        return services;
    }
}
