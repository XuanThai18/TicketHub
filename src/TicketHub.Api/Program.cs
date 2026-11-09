using Serilog;
using TicketHub.Api.Extensions;
using TicketHub.Api.Middlewares;
using TicketHub.Api.Options;
using TicketHub.Application;
using TicketHub.Infrastructure;

// Bootstrap logger: bắt được cả lỗi xảy ra trước khi host dựng xong (thiếu config, ValidateOnStart...).
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.AddObservability("TicketHub.Api");

    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    builder.Services.AddJwtAuthentication();
    builder.Services.AddSecurity(builder.Configuration);
    builder.Services.AddApiDefaults();

    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
        context.ProblemDetails.Extensions["traceId"] =
            System.Diagnostics.Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
    });

    var app = builder.Build();

    await app.ApplyMigrationsIfEnabledAsync();

    // ForwardedHeaders phải chạy đầu tiên để IP/scheme đúng cho log, rate limit, HTTPS redirect.
    app.UseForwardedHeaders();
    // Request logging nằm NGOÀI exception handler để ghi status cuối cùng (400/409...) thay vì 500 + exception.
    app.UseSerilogRequestLogging();
    app.UseExceptionHandler();
    app.UseStatusCodePages();

    if (!app.Environment.IsDevelopment())
        app.UseHsts();

    if (app.Configuration.GetValue<bool>($"{SecurityOptions.SectionName}:{nameof(SecurityOptions.UseHttpsRedirection)}"))
        app.UseHttpsRedirection();

    app.UseCors(SecuritySetup.CorsPolicy);
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();

    app.MapHealthEndpoints();
    app.MapApiDocs();
    app.MapDevTokenEndpoint();
    app.MapControllers();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    // HostAbortedException do "dotnet ef" ném ra khi dừng host có chủ đích, không phải lỗi.
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
