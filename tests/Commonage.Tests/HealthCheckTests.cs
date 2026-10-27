namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class HealthCheckTests(CommonageAppFactory app)
{
    [Fact]
    public async Task Health_endpoint_reports_healthy_when_the_database_is_reachable()
    {
        using var client = app.CreateClient();

        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
