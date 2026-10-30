using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Commonage.Tests;

/// <summary>Runs the real app against a throwaway PostgreSQL container shared by all tests.</summary>
public class CommonageAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Test-only account, created by the app's initial-admin seeding in the empty test database.
    public const string AdminEmail = "admin@example.org";
    public const string AdminPassword = "Test-only-password-1";

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), "commonage-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Connects as the container's superuser, for tests that set up databases of their own.</summary>
    public string ConnectionString => _db.GetConnectionString();

    public Task InitializeAsync() => _db.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _db.DisposeAsync();
        if (Directory.Exists(_storageRoot))
            Directory.Delete(_storageRoot, recursive: true);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", _db.GetConnectionString());
        builder.UseSetting("InitialAdmin:Email", AdminEmail);
        builder.UseSetting("InitialAdmin:Password", AdminPassword);
        builder.UseSetting("Storage:RootPath", _storageRoot);
    }
}

[CollectionDefinition(nameof(AppCollection))]
public class AppCollection : ICollectionFixture<CommonageAppFactory>;
