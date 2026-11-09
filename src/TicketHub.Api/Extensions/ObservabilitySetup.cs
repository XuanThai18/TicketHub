using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

namespace TicketHub.Api.Extensions;

public static class ObservabilitySetup
{
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder, string defaultServiceName)
    {
        // Console: chữ thường ở dev, JSON ở môi trường khác. Không ghi file vì log trong container sẽ mất.
        // Sink bổ sung (Seq, Elastic...) khai báo qua section "Serilog:WriteTo".
        var isDevelopment = builder.Environment.IsDevelopment();
        builder.Services.AddSerilog((services, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithMachineName();

            if (isDevelopment)
                configuration.WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                    formatProvider: System.Globalization.CultureInfo.InvariantCulture);
            else
                configuration.WriteTo.Console(new RenderedCompactJsonFormatter());
        });

        // Chỉ bật OpenTelemetry khi có collector, tránh exporter retry và log rác.
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(otlpEndpoint))
            return builder;

        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? defaultServiceName;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter());

        return builder;
    }
}
