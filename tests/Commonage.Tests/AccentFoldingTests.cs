using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Commonage.Tests;

/// <summary>
/// Search must ignore accents on servers without the unaccent extension, and switch to it once it is there.
/// Each test migrates a database of its own, because the app's shared test database already uses unaccent.
/// </summary>
[Collection(nameof(AppCollection))]
public class AccentFoldingTests(CommonageAppFactory app)
{
    [Fact]
    public async Task Without_the_extension_search_still_ignores_accents()
    {
        await using var db = await CreateMigratedDatabaseAsync();
        await SeedLanguageSamplesAsync(db);

        Assert.False(await HasUnaccentAsync(db));
        await AssertLanguageSamplesAreFoundAsync(db);
    }

    [Fact]
    public async Task Switches_to_the_extension_once_it_is_available_and_reindexes_existing_items()
    {
        await using var db = await CreateMigratedDatabaseAsync();
        await SeedLanguageSamplesAsync(db);
        // The ligature ﬁ is one of the letters only unaccent spells out.
        await SeedAsync(db, "ﬁshing boats");
        Assert.Empty(await SearchAsync(db, "fishing"));

        Assert.True(await AccentFolding.TryUseUnaccentAsync(db, NullLogger.Instance));

        Assert.True(await HasUnaccentAsync(db));
        Assert.Equal(["ﬁshing boats"], await SearchAsync(db, "fishing"));
        await AssertLanguageSamplesAreFoundAsync(db);
        Assert.True(await AccentFolding.TryUseUnaccentAsync(db, NullLogger.Instance));
    }

    [Fact]
    public async Task Keeps_the_built_in_folding_when_the_database_user_may_not_create_extensions()
    {
        await using var db = await CreateMigratedDatabaseAsync(withRestrictedOwner: true);
        await SeedAsync(db, "Die Mühle");

        Assert.False(await AccentFolding.TryUseUnaccentAsync(db, NullLogger.Instance));

        Assert.False(await HasUnaccentAsync(db));
        Assert.Equal(["Die Mühle"], await SearchAsync(db, "muhle"));
    }

    /// <summary>
    /// Creates and migrates a new database. A restricted owner may create tables, as the app needs,
    /// but not extensions, as on hosts that only allow extensions an administrator installs.
    /// </summary>
    private async Task<CommonageDbContext> CreateMigratedDatabaseAsync(bool withRestrictedOwner = false)
    {
        var name = "fold_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = name };

        await using (var admin = new NpgsqlConnection(app.ConnectionString))
        {
            await admin.OpenAsync();
            var setup = $"CREATE DATABASE {name}";
            if (withRestrictedOwner)
            {
                // Test-only role in a throwaway container.
                connection.Username = name;
                connection.Password = Guid.NewGuid().ToString("N");
                setup = $"""
                    CREATE ROLE {name} LOGIN PASSWORD '{connection.Password}';
                    CREATE DATABASE {name} OWNER {name};
                    REVOKE CREATE ON DATABASE {name} FROM {name};
                    """;
            }

            foreach (var statement in setup.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await using var command = new NpgsqlCommand(statement, admin);
                await command.ExecuteNonQueryAsync();
            }
        }

        var db = new CommonageDbContext(new DbContextOptionsBuilder<CommonageDbContext>().UseNpgsql(connection.ConnectionString).Options);
        await db.Database.MigrateAsync();
        return db;
    }

    private static Task SeedLanguageSamplesAsync(CommonageDbContext db) => SeedAsync(db,
        "Die Mühle", "Mühlenstraße", "Café am Markt", "Øresund", "Łódź", "Smørrebrød",
        "Mu\u0308hle (decomposed)", "हिन्दी भाषा", "がっこうの写真", "서울에서", "كِتَاب قديم", "كـتاب");

    /// <summary>Accents and vowel marks never matter, whichever way round, and words in other scripts stay whole.</summary>
    private static async Task AssertLanguageSamplesAreFoundAsync(CommonageDbContext db)
    {
        Assert.Equal(["Die Mühle", "Mu\u0308hle (decomposed)", "Mühlenstraße"], await SearchAsync(db, "muhle"));
        Assert.Equal(["Die Mühle", "Mu\u0308hle (decomposed)", "Mühlenstraße"], await SearchAsync(db, "Mu\u0308hle"));
        Assert.Equal(["Mühlenstraße"], await SearchAsync(db, "MUHLENSTRASSE"));
        Assert.Equal(["Café am Markt"], await SearchAsync(db, "cafe"));
        Assert.Equal(["Café am Markt"], await SearchAsync(db, "Café"));
        Assert.Equal(["Øresund"], await SearchAsync(db, "oresund"));
        Assert.Equal(["Łódź"], await SearchAsync(db, "lodz"));
        Assert.Equal(["Smørrebrød"], await SearchAsync(db, "smorrebrod"));
        Assert.Equal(["हिन्दी भाषा"], await SearchAsync(db, "हिन्दी"));
        Assert.Equal(["がっこうの写真"], await SearchAsync(db, "がっこう"));
        Assert.Equal(["서울에서"], await SearchAsync(db, "서울"));
        Assert.Equal(["كـتاب", "كِتَاب قديم"], await SearchAsync(db, "كتاب"));
    }

    private static async Task SeedAsync(CommonageDbContext db, params string[] titles)
    {
        var now = DateTime.UtcNow;
        var collection = new Collection { Title = "Accents", CreatedAt = now };
        db.AddRange(titles.Select(title => new ArchiveItem { Title = title, Collection = collection, CreatedAt = now, UpdatedAt = now }));
        await db.SaveChangesAsync();
    }

    private static async Task<string[]> SearchAsync(CommonageDbContext db, string text) =>
        (await db.ArchiveItems
            .Filter(new ItemFilter(await db.ToSearchQueryAsync(text)))
            .Select(i => i.Title)
            .ToListAsync())
        .Order()
        .ToArray();

    private static Task<bool> HasUnaccentAsync(CommonageDbContext db) =>
        db.Database.SqlQuery<bool>($"""SELECT EXISTS (SELECT FROM pg_extension WHERE extname = 'unaccent') AS "Value" """).SingleAsync();
}
