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

    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:19-alpine").Build();
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), "commonage-browser-tests", Guid.NewGuid().ToString("N"));
    private IPlaywright? _playwright;
    private IBrowserContext? _lastContext;

    public IBrowser Chrome { get; private set; } = null!;

    public string BaseUrl => ClientOptions.BaseAddress.ToString().TrimEnd('/');

    /// <summary>The address of the demo archive's contribution link.</summary>
    public string ContributionPath { get; private set; } = "";

    /// <summary>The address of the demo's public person page (Karl, who has died).</summary>
    public string PersonPath { get; private set; } = "";

    /// <summary>The address a page in the lists stands for: "CONTRIBUTE" and "PERSON" are only known once the demo archive exists.</summary>
    public string Resolve(string path) => path switch { "CONTRIBUTE" => ContributionPath, "PERSON" => PersonPath, "MOVE" => MovePath, "MOVE-CONFIRM" => MovePath + "&newName=Someone+new", _ => path };

    /// <summary>The page for moving the items of the first person who has died, with its first item ticked.</summary>
    public string MovePath { get; private set; } = "";

    /// <summary>How many contributions with this title have arrived, and how many photos they have together.</summary>
    public async Task<(long Contributions, long Photos)> CountContributionsAsync(string title)
    {
        await using var connection = new NpgsqlConnection(_db.GetConnectionString());
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("""
            select count(distinct i."Id"), count(f."Id")
            from "Contributions" c
            join "ArchiveItems" i on i."Id" = c."ArchiveItemId"
            left join "MediaFiles" f on f."ArchiveItemId" = i."Id"
            where i."Title" = @title
            """, connection);
        query.Parameters.AddWithValue("title", title);
        await using var reader = await query.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>People as the migration of the person tags leaves them: nobody has chosen who may see them yet. Returns their ids.</summary>
    public async Task<List<int>> AddUnreviewedPeopleAsync(params string[] names)
    {
        var ids = new List<int>();
        await using var connection = new NpgsqlConnection(_db.GetConnectionString());
        await connection.OpenAsync();
        foreach (var name in names)
        {
            await using var insert = new NpgsqlCommand("""
                insert into "People" ("PublicId", "Name", "Disclosure", "DisclosureChangedAt", "CreatedAt", "UpdatedAt")
                values (substr(replace(gen_random_uuid()::text, '-', ''), 1, 12), @name, 'Hidden', null, now(), now()) returning "Id"
                """, connection);
            insert.Parameters.AddWithValue("name", name);
            ids.Add((int)(await insert.ExecuteScalarAsync())!);
        }

        return ids;
    }

    /// <summary>A person curators chose to hide, linked to demo items 1 and 2.</summary>
    public async Task<int> AddLinkedPersonAsync(string name)
    {
        await using var connection = new NpgsqlConnection(_db.GetConnectionString());
        await connection.OpenAsync();
        await using var insert = new NpgsqlCommand("""
            with p as (
                insert into "People" ("PublicId", "Name", "Disclosure", "DisclosureChangedAt", "CreatedAt", "UpdatedAt")
                values (substr(replace(gen_random_uuid()::text, '-', ''), 1, 12), @name, 'Hidden', now(), now(), now()) returning "Id"),
            l as (
                insert into "ItemPeople" ("ArchiveItemId", "PersonId", "CreatedAt") select i, p."Id", now() from p, unnest(array[1, 2]) as i)
            select "Id" from p
            """, connection);
        insert.Parameters.AddWithValue("name", name);
        return (int)(await insert.ExecuteScalarAsync())!;
    }

    /// <summary>The items a person is linked to, in the bin or not, and whether the person is in the bin; null when there is no such person.</summary>
    public async Task<(int Items, bool InBin)?> PersonStateAsync(string name)
    {
        await using var connection = new NpgsqlConnection(_db.GetConnectionString());
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("""
            select (select count(*) from "ItemPeople" l where l."PersonId" = p."Id"), p."DeletedAt" is not null from "People" p where p."Name" = @name
            """, connection);
        query.Parameters.AddWithValue("name", name);
        await using var reader = await query.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ((int)reader.GetInt64(0), reader.GetBoolean(1)) : null;
    }

    /// <summary>Who may see a person, as stored, and whether somebody has chosen it.</summary>
    public async Task<(string Disclosure, bool Chosen)> DisclosureOfAsync(int id)
    {
        await using var connection = new NpgsqlConnection(_db.GetConnectionString());
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("""select "Disclosure", "DisclosureChangedAt" is not null from "People" where "Id" = @id""", connection);
        query.Parameters.AddWithValue("id", id);
        await using var reader = await query.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetString(0), reader.GetBoolean(1));
    }

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
            await using var person = new NpgsqlCommand("""select "PublicId" from "People" where "Disclosure" = 'Deceased' and "DeletedAt" is null limit 1""", connection);
            PersonPath = $"/people/{await person.ExecuteScalarAsync()}";
            await using var moving = new NpgsqlCommand("""select p."Id", l."ArchiveItemId" from "ItemPeople" l join "People" p on p."Id" = l."PersonId" where p."Disclosure" = 'Deceased' and p."DeletedAt" is null order by p."Id" limit 1""", connection);
            await using (var reader = await moving.ExecuteReaderAsync())
            {
                await reader.ReadAsync();
                MovePath = $"/Admin/People/Move/{reader.GetInt32(0)}?items={reader.GetInt32(1)}";
            }

            // A person in the bin, so that the bin shows its section for people.
            await using var binned = new NpgsqlCommand("""
                insert into "People" ("PublicId", "Name", "Disclosure", "DisclosureChangedAt", "CreatedAt", "UpdatedAt", "DeletedAt")
                values (substr(replace(gen_random_uuid()::text, '-', ''), 1, 12), 'Binned person', 'Hidden', now(), now(), now(), now())
                """, connection);
            await binned.ExecuteNonQueryAsync();

            await using var unreviewed = new NpgsqlCommand("""
                insert into "People" ("PublicId", "Name", "Disclosure", "DisclosureChangedAt", "CreatedAt", "UpdatedAt")
                select substr(replace(gen_random_uuid()::text, '-', ''), 1, 12), n, 'Hidden', null, now(), now() from unnest(array['Former tag Anna', 'Former tag Berta']) as n
                """, connection);
            await unreviewed.ExecuteNonQueryAsync();

            // "Do you know more about this?" is off by default; its pages are tested here.
            await using var notes = new NpgsqlCommand("""update "ArchiveSettings" set "AcceptVisitorNotes" = true""", connection);
            await notes.ExecuteNonQueryAsync();
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
