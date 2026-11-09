using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using TicketHub.Api.Options;

namespace TicketHub.Api.Extensions;

public static class SecuritySetup
{
    public const string CorsPolicy = "Default";
    public const string SensitivePolicy = "sensitive";

    public static IServiceCollection AddSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        var securitySection = configuration.GetSection(SecurityOptions.SectionName);
        var security = securitySection.Get<SecurityOptions>() ?? new();
        services.Configure<SecurityOptions>(securitySection);

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var cidr in security.KnownNetworks)
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
        });

        var cors = configuration.GetSection(CorsOptions.SectionName).Get<CorsOptions>() ?? new();
        services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
        {
            if (cors.AllowedOrigins.Length == 0)
                return;

            policy.WithOrigins(cors.AllowedOrigins).AllowAnyHeader().AllowAnyMethod();
            // Cần khi FE khác origin mà dùng cookie (refresh token HttpOnly); AllowedOrigins phải liệt kê cụ thể.
            if (cors.AllowCredentials)
                policy.AllowCredentials();
        }));

        services.AddOptions<RateLimitingOptions>()
            .BindConfiguration(RateLimitingOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var rateLimit = configuration.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>() ?? new();
        services.AddRateLimiter(options =>
        {
            // Đếm theo user đã đăng nhập, nếu không thì theo IP (đã qua ForwardedHeaders).
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.User.Identity?.IsAuthenticated == true
                        ? "user:" + context.User.Identity.Name
                        : "ip:" + context.Connection.RemoteIpAddress,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = rateLimit.PermitLimit,
                        Window = TimeSpan.FromSeconds(rateLimit.WindowSeconds),
                        QueueLimit = 0
                    }));

            // Chặt hơn cho endpoint nhạy cảm (đăng nhập, refresh): chống dò mật khẩu theo IP.
            options.AddPolicy(SensitivePolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    "sensitive-ip:" + context.Connection.RemoteIpAddress,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = rateLimit.SensitivePermitLimit,
                        Window = TimeSpan.FromSeconds(rateLimit.WindowSeconds),
                        QueueLimit = 0
                    }));

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);

                var problemDetails = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
                await problemDetails.WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = context.HttpContext,
                    ProblemDetails = new ProblemDetails
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = "Too Many Requests",
                        Detail = "Rate limit exceeded. Please retry later."
                    }
                });
            };
        });

        services.AddHsts(options =>
        {
            options.MaxAge = TimeSpan.FromDays(365);
            options.IncludeSubDomains = true;
        });

        return services;
    }
}
