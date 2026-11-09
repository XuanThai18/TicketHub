using System.Net;

namespace TicketHub.IntegrationTests;

public sealed class InfrastructureEndpointsTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_AreAnonymousAndHealthy(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnknownRoute_RequiresAuthenticationFirst()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/does-not-exist", Ct);

        // Secure-by-default: fallback policy chặn trước khi lộ route nào tồn tại.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DevTokenEndpoint_IsNotMappedOutsideDevelopment()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/dev/token", null, Ct);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
