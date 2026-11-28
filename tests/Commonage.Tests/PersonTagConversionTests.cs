using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Commonage.Tests;

/// <summary>
/// The migration that makes every tag of the kind Person a person (M30, part 3), run on a database as it was before: migrated to
/// <c>AddPeople</c>, seeded with raw SQL, migrated on.
/// </summary>
[Collection(nameof(AppCollection))]
public class PersonTagConversionTests(CommonageAppFactory app)
{
    private const string BeforeConversion = "20261126180443_AddPeople";

    private sealed record Seeded(string Name, string Kind, params (string Title, int Status, string? PublicFrom, bool Binned)[] Items);

    /// <summary>Makes a database of its own, at the migration before the conversion, and seeds tags with the items they are on.</summary>
    private async Task<(CommonageDbContext Db, IMigrator Migrator)> DatabaseBeforeAsync(params Seeded[] tags)
    {
        var name = "conversion_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(app.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = name }.ConnectionString;
        var db = new CommonageDbContext(new DbContextOptionsBuilder<CommonageDbContext>().UseNpgsql(connectionString).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeConversion);

        await db.Database.ExecuteSqlRawAsync("""INSERT INTO "Collections" ("Title", "CreatedAt") VALUES ('Town', now());""");
        foreach (var tag in tags)
        {
            await db.Database.ExecuteSqlRawAsync("""INSERT INTO "Tags" ("Name", "NormalizedName", "Kind") VALUES ({0}, {1}, {2});""", tag.Name, Tag.Normalize(tag.Name), tag.Kind == "Person" ? 2 : tag.Kind == "Place" ? 1 : 0);
            foreach (var item in tag.Items)
            {
                // The same title is the same item: it can carry several tags.
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "ArchiveItems" ("Title", "Status", "CollectionId", "CreatedAt", "UpdatedAt", "NeedsTranscription", "PublicFrom", "DeletedAt")
                    SELECT {0}, {1}, (SELECT min("Id") FROM "Collections"), now(), now(), false, {2}::date, CASE WHEN {3} THEN now() END
                     WHERE NOT EXISTS (SELECT 1 FROM "ArchiveItems" WHERE "Title" = {0});
                    """, item.Title, item.Status, (object?)item.PublicFrom ?? DBNull.Value, item.Binned);
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "ArchiveItemTag" ("ItemsId", "TagsId")
                    SELECT i."Id", t."Id" FROM "ArchiveItems" i, "Tags" t WHERE i."Title" = {0} AND t."Name" = {1};
                    UPDATE "ArchiveItems" i SET "TagNames" = (SELECT string_agg(t."Name", ' ' ORDER BY t."Id") FROM "ArchiveItemTag" it JOIN "Tags" t ON t."Id" = it."TagsId" WHERE it."ItemsId" = i."Id")
                     WHERE i."Title" = {0};
                    """, item.Title, tag.Name);
            }
        }

        return (db, migrator);
    }

    private static string Tomorrow => DateTime.UtcNow.AddDays(2).ToString("yyyy-MM-dd");
    private static string LongAgo => "2001-02-03";

    [Fact]
    public async Task Every_person_tag_becomes_a_person_by_what_visitors_could_see_of_its_items()
    {
        var (db, migrator) = await DatabaseBeforeAsync(
            new Seeded("Only draft", "Person", ("A draft", 0, null, false)),
            new Seeded("On a published item", "Person", ("A published item", 1, null, false)),
            new Seeded("Only binned", "Person", ("A binned item", 1, null, true)),
            new Seeded("Visible and draft", "Person", ("Visible one", 1, LongAgo, false), ("Draft one", 0, null, false)),
            new Seeded("Embargoed", "Person", ("An embargoed item", 1, Tomorrow, false)),
            new Seeded("No items", "Person"),
            new Seeded("Mill Street", "Place", ("A published item", 1, null, false)),
            new Seeded("Flood", "Topic", ("A published item", 1, null, false)));
        await using var _ = db;

        await migrator.MigrateAsync();

        var people = await db.People.IgnoreQueryFilters().ToDictionaryAsync(p => p.Name);
        Assert.Equal(PersonDisclosure.Hidden, people["Only draft"].Disclosure);
        Assert.Equal(PersonDisclosure.MayBeShown, people["On a published item"].Disclosure);
        Assert.Equal(PersonDisclosure.Hidden, people["Only binned"].Disclosure);
        Assert.Equal(PersonDisclosure.MayBeShown, people["Visible and draft"].Disclosure);
        Assert.Equal(PersonDisclosure.Hidden, people["Embargoed"].Disclosure);
        Assert.Equal(PersonDisclosure.Hidden, people["No items"].Disclosure);
        Assert.Equal(6, people.Count);
        // Nobody has chosen yet: this is what the review list and the card of the dashboard look for.
        Assert.All(people.Values, p => Assert.Null(p.DisclosureChangedAt));
        Assert.All(people.Values, p => Assert.Matches("^[0-9a-hjkmnp-tv-z]{12}$", p.PublicId));
        Assert.Equal(6, people.Values.Select(p => p.PublicId).Distinct().Count());
        Assert.All(people.Values, p => Assert.Null(p.DeletedAt));
    }

    [Fact]
    public async Task The_links_stay_the_bin_included_and_the_tags_leave_the_items_and_the_search_words()
    {
        var (db, migrator) = await DatabaseBeforeAsync(
            new Seeded("Only binned", "Person", ("A binned item", 1, null, true)),
            new Seeded("Two items", "Person", ("First item", 1, null, false), ("Second item", 0, null, false)),
            new Seeded("Mill Street", "Place", ("First item", 1, null, false)),
            new Seeded("Flood", "Topic", ("Second item", 0, null, false)));
        await using var _ = db;

        await migrator.MigrateAsync();

        var links = await db.ItemPeople.IgnoreQueryFilters()
            .Select(l => new { Person = l.Person.Name, l.ArchiveItem.Title }).ToListAsync();
        Assert.Equal(
            ["Only binned|A binned item", "Two items|First item", "Two items|Second item"],
            links.Select(l => $"{l.Person}|{l.Title}").Order().ToList());

        Assert.Equal(["Flood", "Mill Street"], await db.Tags.Select(t => t.Name).OrderBy(n => n).ToListAsync());
        var tagNames = await db.ArchiveItems.IgnoreQueryFilters().ToDictionaryAsync(i => i.Title, i => i.TagNames);
        Assert.Equal("Mill Street", tagNames["First item"]);
        Assert.Equal("Flood", tagNames["Second item"]);
        Assert.True(string.IsNullOrEmpty(tagNames["A binned item"]));
        var joined = await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "ArchiveItemTag" """).SingleAsync();
        Assert.Equal(2, joined);
    }

    [Fact]
    public async Task A_name_that_is_also_a_topic_tag_elsewhere_keeps_the_topic()
    {
        // The same name without its accent is another tag, so it is a topic and a person at once until now.
        var (db, migrator) = await DatabaseBeforeAsync(
            new Seeded("José Weber", "Person", ("A published item", 1, null, false)),
            new Seeded("Jose Weber", "Topic", ("A published item", 1, null, false), ("Another item", 1, null, false)));
        await using var _ = db;

        await migrator.MigrateAsync();

        Assert.Equal(["Jose Weber"], await db.Tags.Select(t => t.Name).ToListAsync());
        Assert.Equal(["José Weber"], await db.People.Select(p => p.Name).ToListAsync());
        Assert.Equal(2, await db.ArchiveItems.CountAsync(i => i.Tags.Any(t => t.Name == "Jose Weber")));
        Assert.Equal("Jose Weber", (await db.ArchiveItems.SingleAsync(i => i.Title == "A published item")).TagNames);
    }

    [Fact]
    public async Task One_line_of_the_activity_log_says_so_and_none_when_there_was_nothing_to_convert()
    {
        var (withTags, migrator) = await DatabaseBeforeAsync(new Seeded("A", "Person", ("An item", 0, null, false)), new Seeded("B", "Person", ("An item", 0, null, false)));
        await using var _ = withTags;
        await migrator.MigrateAsync();
        var entries = await withTags.AuditEntries.Where(e => e.Action == AuditAction.PersonTagsConverted).ToListAsync();
        Assert.Single(entries);
        Assert.Null(entries[0].Subject);
        Assert.Null(entries[0].PersonId);

        var (without, otherMigrator) = await DatabaseBeforeAsync(new Seeded("Flood", "Topic", ("An item", 0, null, false)));
        await using var __ = without;
        await otherMigrator.MigrateAsync();
        Assert.Empty(await without.AuditEntries.Where(e => e.Action == AuditAction.PersonTagsConverted).ToListAsync());
    }

    /// <summary>
    /// The migration writes the item rule in SQL, and the pages use <see cref="Visibility.Item"/>. They must pick the same items, except
    /// for the one deliberate difference: an embargo that ends today is not yet enough for the migration, which can only keep a person hidden.
    /// </summary>
    [Fact]
    public async Task The_rule_of_the_migration_and_Visibility_Item_pick_the_same_items()
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var statuses = new[] { 0, 1, 2, 3, 4 };
        var dates = new string?[] { null, LongAgo, today, Tomorrow };
        var seeds = new List<Seeded>();
        foreach (var status in statuses)
            foreach (var date in dates)
                foreach (var binned in new[] { false, true })
                    seeds.Add(new Seeded($"Person s{status} d{date ?? "none"} b{binned}", "Person", ($"Item s{status} d{date ?? "none"} b{binned}", status, date, binned)));
        var (db, migrator) = await DatabaseBeforeAsync([.. seeds]);
        await using var _ = db;
        await migrator.MigrateAsync();

        var todayOnly = DateOnly.FromDateTime(await db.Database.SqlQueryRaw<DateTime>("""SELECT CURRENT_DATE::timestamp AS "Value" """).SingleAsync());
        var byCode = await db.ArchiveItems.Where(Visibility.Item(todayOnly)).Select(i => i.Title).ToListAsync();
        var byMigration = await db.ItemPeople.Where(l => l.Person.Disclosure == PersonDisclosure.MayBeShown).Select(l => l.ArchiveItem.Title).ToListAsync();

        Assert.NotEmpty(byCode);
        Assert.Equal(byCode.Where(t => !t.Contains($" d{today} ")).Order().ToList(), byMigration.Order().ToList());
        // Every item that visitors cannot see keeps its person hidden.
        Assert.Empty(byMigration.Except(byCode));
        Assert.Equal(seeds.Count, await db.People.IgnoreQueryFilters().CountAsync());
    }
}
