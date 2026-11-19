using System.IO.Compression;
using System.Text.Json;
using Commonage.Web;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Identity;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

/// <summary>An embargo: a published item that is not public until the start of a day in the archive's time zone.</summary>
[Collection(nameof(AppCollection))]
public class EmbargoTests(CommonageAppFactory app)
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private async Task<(string Word, int ItemId, int CollectionId)> SeedAsync(ItemStatus status, DateOnly? publicFrom = null)
    {
        var word = TestData.UniqueWord("embargo");
        return await app.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = $"Box {word}", CreatedAt = now };
            var item = new ArchiveItem { Title = $"Title {word}", Collection = collection, CreatedAt = now, UpdatedAt = now, PublicFrom = publicFrom };
            if (status == ItemStatus.Published)
                item.Publish(now);
            db.AddRange(collection, item);
            await db.SaveChangesAsync();
            return (word, item.Id, collection.Id);
        });
    }

    [Fact]
    public async Task An_item_opens_at_the_start_of_its_day_in_the_archives_time_zone_and_not_a_second_before()
    {
        // Auckland is 13 hours ahead of UTC in March, so 1 March starts at 11:00 UTC on 28 February.
        Dates.Zone.Value = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        try
        {
            var seeded = await SeedAsync(ItemStatus.Published, new DateOnly(2030, 3, 1));
            var options = new DbContextOptionsBuilder<CommonageDbContext>().UseNpgsql(app.ConnectionString).Options;
            async Task<bool> VisibleAtAsync(DateTimeOffset utc)
            {
                await using var db = new CommonageDbContext(options, new FixedClock(utc));
                return await db.VisibleItems.AnyAsync(i => i.Id == seeded.ItemId);
            }

            Assert.False(await VisibleAtAsync(new DateTimeOffset(2030, 2, 28, 10, 59, 59, TimeSpan.Zero)));
            Assert.True(await VisibleAtAsync(new DateTimeOffset(2030, 2, 28, 11, 0, 0, TimeSpan.Zero)));
        }
        finally
        {
            Dates.Zone.Value = null;
        }
    }

    [Fact]
    public void An_embargo_moves_the_date_an_item_is_listed_by_to_the_day_it_opens_and_removing_it_brings_it_back()
    {
        Dates.Zone.Value = TimeZoneInfo.Utc;
        try
        {
            var now = new DateTime(2030, 1, 10, 12, 0, 0, DateTimeKind.Utc);
            var item = new ArchiveItem { Title = "A" };
            item.Publish(now);

            item.SetPublicFrom(new DateOnly(2030, 2, 1), now);
            Assert.Equal(new DateTime(2030, 2, 1, 0, 0, 0, DateTimeKind.Utc), item.PublishedAt!.Value.ToUniversalTime());

            item.SetPublicFrom(new DateOnly(2030, 1, 20), now);
            Assert.Equal(new DateTime(2030, 1, 20, 0, 0, 0, DateTimeKind.Utc), item.PublishedAt!.Value.ToUniversalTime());

            item.SetPublicFrom(new DateOnly(2030, 1, 1), now);
            Assert.Equal(now, item.PublishedAt);

            item.SetPublicFrom(new DateOnly(2030, 2, 1), now);
            item.SetPublicFrom(null, now);
            Assert.Equal(now, item.PublishedAt);
        }
        finally
        {
            Dates.Zone.Value = null;
        }
    }

    [Fact]
    public async Task A_curator_sets_the_date_and_it_is_recorded_while_a_helper_cannot()
    {
        var seeded = await SeedAsync(ItemStatus.Draft);
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (helper, _, _, _) = await app.CreateAccountAsync(admin, Roles.Helper);
        var page = $"/Admin/Items/Edit/{seeded.ItemId}";
        Dictionary<string, string> Form() => new()
        {
            ["Input.Title"] = $"Title {seeded.Word}", ["Input.CollectionId"] = seeded.CollectionId.ToString(), ["Input.PublicFrom"] = "2031-05-04",
        };

        await Browser.SendFormAsync(helper, page, Form(), page);
        var afterHelper = await app.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == seeded.ItemId).Select(i => i.PublicFrom).SingleAsync());
        await Browser.SendFormAsync(admin, page, Form(), page);
        var afterCurator = await app.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == seeded.ItemId).Select(i => i.PublicFrom).SingleAsync());
        var recorded = await app.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == AuditAction.ItemPublicFromChanged && e.ArchiveItemId == seeded.ItemId));

        Assert.Null(afterHelper);
        Assert.Equal(new DateOnly(2031, 5, 4), afterCurator);
        Assert.True(recorded);
    }

    [Fact]
    public async Task The_public_copy_of_the_export_leaves_out_an_embargoed_item_and_the_full_export_keeps_its_date()
    {
        var embargoed = await SeedAsync(ItemStatus.Published, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30));
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);

        var published = await ItemIdsAsync(admin, publishedOnly: true);
        var full = await ItemsAsync(admin, publishedOnly: false);

        Assert.DoesNotContain(embargoed.ItemId, published);
        Assert.NotNull(full.Single(i => i.GetProperty("id").GetInt32() == embargoed.ItemId).GetProperty("publicFrom").GetString());
    }

    [Fact]
    public async Task Many_items_can_be_embargoed_and_set_to_record_only_together_and_the_list_finds_them()
    {
        var one = await SeedAsync(ItemStatus.Published);
        var two = await SeedAsync(ItemStatus.Published);
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var ids = $"{one.ItemId},{two.ItemId}";

        var token = Browser.Token(await admin.GetStringAsync("/Admin"));
        await admin.PostAsync("/Admin/Items/Bulk?handler=Apply", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["__RequestVerificationToken"] = token, ["Action"] = "SetPublicFrom", ["Ids"] = ids, ["PublicFrom"] = "2099-01-01" }));
        await admin.PostAsync("/Admin/Items/Bulk?handler=Apply", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["__RequestVerificationToken"] = token, ["Action"] = "SetMediaLevel", ["Ids"] = ids, ["MediaLevel"] = "RecordOnly" }));
        var state = await app.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == one.ItemId || i.Id == two.ItemId).Select(i => new { i.PublicFrom, i.MediaLevel }).ToListAsync());
        var embargoedList = await admin.GetStringAsync($"/Admin?needs=Embargoed&q=embargo");
        var recordList = await admin.GetStringAsync($"/Admin?needs=RecordOnly&q=embargo");

        Assert.All(state, s => { Assert.Equal(new DateOnly(2099, 1, 1), s.PublicFrom); Assert.Equal(MediaLevel.RecordOnly, s.MediaLevel); });
        Assert.Contains($"Title {one.Word}", embargoedList);
        Assert.Contains($"Title {two.Word}", recordList);
    }

    private static async Task<List<int>> ItemIdsAsync(HttpClient admin, bool publishedOnly) =>
        (await ItemsAsync(admin, publishedOnly)).Select(i => i.GetProperty("id").GetInt32()).ToList();

    private static async Task<List<JsonElement>> ItemsAsync(HttpClient admin, bool publishedOnly)
    {
        var response = await Browser.PostFormAsync(admin, "/Admin/Export", new() { ["PublishedOnly"] = publishedOnly ? "true" : "false" });
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var zip = new ZipArchive(stream);
        using var reader = new StreamReader(zip.GetEntry("archive.json")!.Open());
        return JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.GetProperty("items").EnumerateArray().Select(i => i.Clone()).ToList();
    }
}
