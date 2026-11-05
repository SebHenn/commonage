using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Playwright;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Commonage.BrowserTests;

/// <summary>Runs the real app with the demo archive on a real port, and the installed Chrome to look at it.</summary>
public sealed class BrowserFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@example.org";
    public const string AdminPassword = "Browser-test-password-1";

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), "commonage-browser-tests", Guid.NewGuid().ToString("N"));
    private IPlaywright? _playwright;
    private IBrowserContext? _lastContext;

    public IBrowser Chrome { get; private set; } = null!;

    public string BaseUrl => ClientOptions.BaseAddress.ToString().TrimEnd('/');

    /// <summary>The address of the demo archive's contribution link.</summary>
    public string ContributionPath { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        UseKestrel(0);
        StartServer();

        await using (var connection = new NpgsqlConnection(_db.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var query = new NpgsqlCommand("""select "Token" from "ContributionLinks" limit 1""", connection);
            ContributionPath = $"/contribute/{await query.ExecuteScalarAsync()}";
        }

        _playwright = await Playwright.CreateAsync();
        Chrome = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Channel = "chrome" });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (Chrome is not null)
            await Chrome.CloseAsync();
        _playwright?.Dispose();
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
        builder.UseSetting("Demo:Language", "en");
        builder.UseSetting("Contributions:MinimumFillSeconds", "0");
        builder.UseSetting("Contributions:MaxSubmissionsPerHour", "1000");
        builder.UseSetting("Login:MaxAttemptsPerHour", "100000");
        builder.UseSetting("Uploads:MinimumFreeMegabytes", "1");
        // Right to left, and a made-up language whose text is much longer than English.
        builder.UseSetting("Languages:ForTesting", $"ar,{LongTextLanguage.Code}");
        builder.ConfigureTestServices(services => services.AddSingleton<IStringLocalizerFactory>(provider =>
            new LongTextLanguage(ActivatorUtilities.CreateInstance<ResourceManagerStringLocalizerFactory>(provider))));
    }

    /// <summary>A new browser session of its own, as if on another device.</summary>
    public async Task<IPage> NewPageAsync(int width = 1280, int height = 800, bool javaScript = true)
    {
        // The tests run one after the other. Sessions left open would use up the machine's connections.
        if (_lastContext is not null)
            await _lastContext.CloseAsync();

        var context = _lastContext = await Chrome.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            ViewportSize = new ViewportSize { Width = width, Height = height },
            IsMobile = width < 600,
            HasTouch = width < 600,
            JavaScriptEnabled = javaScript,
            Locale = "en",
        });
        return await context.NewPageAsync();
    }

    public async Task<IPage> LogInAsync(int width = 1280, int height = 800, bool javaScript = true)
    {
        var page = await NewPageAsync(width, height, javaScript);
        await page.GotoAsync("/Account/Login");
        await page.FillAsync("[name='Input.Email']", AdminEmail);
        await page.FillAsync("[name='Input.Password']", AdminPassword);
        await page.ClickAsync("main form button[type=submit]");
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login"));
        return page;
    }
}

[CollectionDefinition(nameof(BrowserCollection))]
public class BrowserCollection : ICollectionFixture<BrowserFixture>;
