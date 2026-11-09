using Asp.Versioning;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace TicketHub.Api.Extensions;

public static class ApiSetup
{
    private const string BearerScheme = "Bearer";

    public static IServiceCollection AddApiDefaults(this IServiceCollection services)
    {
        services.AddControllers();

        // Controller không gắn [ApiVersion] được coi là v1 → route api/v1/...
        // Không gọi services.AddOpenApi(): AddOpenApi() của Asp.Versioning tự sinh một tài liệu cho mỗi phiên bản.
        services.AddApiVersioning(options =>
            {
                options.DefaultApiVersion = new ApiVersion(1, 0);
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.ReportApiVersions = true;
                options.ApiVersionReader = new UrlSegmentApiVersionReader();
            })
            .AddMvc()
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'VVV";
                options.SubstituteApiVersionInUrl = true;
            })
            .AddOpenApi(options => options.Document.AddDocumentTransformer((document, _, _) =>
            {
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    In = ParameterLocation.Header
                };
                document.Security ??= [];
                document.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(BearerScheme, document)] = []
                });
                return Task.CompletedTask;
            }));

        return services;
    }

    public static WebApplication MapApiDocs(this WebApplication app)
    {
        // Mặc định chỉ mở ở Development; môi trường khác bật bằng OpenApi:Enabled=true.
        if (!app.Environment.IsDevelopment() && !app.Configuration.GetValue<bool>("OpenApi:Enabled"))
            return app;

        app.MapOpenApi().WithDocumentPerVersion().AllowAnonymous().DisableRateLimiting();
        app.MapScalarApiReference(options =>
            {
                // Bản mới nhất làm mặc định trong UI.
                var descriptions = app.DescribeApiVersions();
                for (var i = 0; i < descriptions.Count; i++)
                {
                    var groupName = descriptions[i].GroupName;
                    options.AddDocument(groupName, groupName, isDefault: i == descriptions.Count - 1);
                }
            })
            .AllowAnonymous()
            .DisableRateLimiting();

        return app;
    }
}
