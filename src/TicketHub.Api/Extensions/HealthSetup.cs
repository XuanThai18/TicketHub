using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TicketHub.Infrastructure;

namespace TicketHub.Api.Extensions;

public static class HealthSetup
{
    public static WebApplication MapHealthEndpoints(this WebApplication app)
    {
        // Liveness không kiểm phụ thuộc: DB chết thì k8s không nên restart pod, chỉ rút khỏi load balancer.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
            .AllowAnonymous()
            .DisableRateLimiting();

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains(DependencyInjection.ReadyTag),
                ResponseWriter = WriteResponse
            })
            .AllowAnonymous()
            .DisableRateLimiting();

        return app;
    }

    private static Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            durationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new { status = e.Value.Status.ToString(), durationMs = e.Value.Duration.TotalMilliseconds })
        }));
    }
}
