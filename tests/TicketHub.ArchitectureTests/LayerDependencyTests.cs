using System.Reflection;
using NetArchTest.Rules;
using TicketHub.Api.Controllers;

namespace TicketHub.ArchitectureTests;

/// <summary>Khoá chiều phụ thuộc Clean Architecture: Api → Infrastructure → Application → Domain.</summary>
public sealed class LayerDependencyTests
{
    private const string Domain = "TicketHub.Domain";
    private const string Application = "TicketHub.Application";
    private const string Infrastructure = "TicketHub.Infrastructure";
    private const string Api = "TicketHub.Api";

    private static readonly Assembly DomainAssembly = typeof(Domain.Shared.Result).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(Application.DependencyInjection).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(Infrastructure.DependencyInjection).Assembly;
    private static readonly Assembly ApiAssembly = typeof(BaseApiController).Assembly;

    [Fact]
    public void Domain_DependsOnNoOtherLayerOrFramework() =>
        AssertNoDependency(DomainAssembly,
            Application, Infrastructure, Api, "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "MediatR", "Npgsql");

    [Fact]
    public void Application_DoesNotDependOnInfrastructureOrWeb() =>
        // EF Core được phép (query handler viết LINQ), nhưng không được dính provider Npgsql hay tầng ngoài.
        AssertNoDependency(ApplicationAssembly, Infrastructure, Api, "Microsoft.AspNetCore", "Npgsql");

    // Phía ghi phải đi qua repository + IUnitOfWork; chỉ phía đọc được truy vấn DB trực tiếp.
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]                     // LINQ qua IReadDbContext
    [InlineData("Dapper")]                                            // SQL tay
    [InlineData("TicketHub.Application.Abstractions.ISqlConnectionFactory")]
    public void Application_OnlyQueryHandlersQueryTheDatabaseDirectly(string dependency)
    {
        // Thân hàm async nằm trong state machine lồng (<Handle>d__N) → quy về class chứa ngoài cùng rồi mới kiểm tên.
        var offenders = Types.InAssembly(ApplicationAssembly)
            .That().HaveDependencyOn(dependency)
            .GetTypes()
            .Select(OutermostType)
            .Where(t => t.FullName != dependency && !t.Name.EndsWith("QueryHandler", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0, "Vi phạm: " + string.Join(", ", offenders));
    }

    private static Type OutermostType(Type type)
    {
        while (type.DeclaringType is not null)
            type = type.DeclaringType;
        return type;
    }

    [Fact]
    public void Infrastructure_DoesNotDependOnApi() =>
        AssertNoDependency(InfrastructureAssembly, Api);

    [Fact]
    public void Handlers_AreSealed()
    {
        var handlers = Types.InAssembly(ApplicationAssembly)
            .That().HaveNameEndingWith("Handler", StringComparison.Ordinal);

        // Bộ lọc rỗng thì luật luôn "đạt" giả → báo Skip cho thấy rõ (project mới chưa có feature nào).
        Assert.SkipWhen(!handlers.GetTypes().Any(), "Chưa có handler nào để kiểm.");

        var result = handlers.Should().BeSealed().GetResult();
        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Controllers_InheritBaseApiController()
    {
        var controllers = Types.InAssembly(ApiAssembly)
            .That().HaveNameEndingWith("Controller", StringComparison.Ordinal)
            .And().AreNotAbstract();

        Assert.SkipWhen(!controllers.GetTypes().Any(), "Chưa có controller nào để kiểm.");

        var result = controllers.Should().Inherit(typeof(BaseApiController)).GetResult();
        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Controllers_DoNotUseInfrastructureDirectly()
    {
        // Controller chỉ nói chuyện với Application qua MediatR; không gọi DbContext/repository.
        var result = Types.InAssembly(ApiAssembly)
            .That().HaveNameEndingWith("Controller", StringComparison.Ordinal)
            .ShouldNot().HaveDependencyOnAny(Infrastructure, "Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static void AssertNoDependency(Assembly assembly, params string[] forbidden)
    {
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(NetArchTest.Rules.TestResult result) =>
        "Vi phạm: " + string.Join(", ", result.FailingTypeNames ?? []);
}
