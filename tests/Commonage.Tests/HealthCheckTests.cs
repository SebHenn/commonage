using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Commonage.Tests;

public class HealthCheckTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public Task InitializeAsync() => _db.StartAsync();

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    [Fact]
    public async Task Health_endpoint_reports_healthy_when_the_database_is_reachable()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host => host.UseSetting("ConnectionStrings:Default", _db.GetConnectionString()));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
