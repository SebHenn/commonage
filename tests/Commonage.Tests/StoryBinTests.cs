using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

/// <summary>
/// Stories in the bin: gone for visitors at once, restorable for 30 days, deleted for good by hand or by the purge. Each test has an
/// archive of its own, so that what is listed, exported and counted is only what the test made.
/// </summary>
[Collection(nameof(AppCollection))]
public class StoryBinTests(CommonageAppFactory app)
{
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Seeded(int StoryId, int ItemId, string StoryTitle, string ItemTitle, string Words);

    /// <summary>A published story with a text and a published item, and a draft story of its own with the same item.</summary>
    private static Task<Seeded> SeedAsync(WebApplicationFactory<Program> archive) => archive.WithDbAsync(async db =>
    {
        var now = DateTime.UtcNow;
        var words = TestData.UniqueWord("binned");
        var collection = new Collection { Title = $"Box {words}", CreatedAt = now };
        var item = new ArchiveItem { Title = $"Flood {words}", Collection = collection, CreatedAt = now, UpdatedAt = now };
        item.Publish(now);
        var story = new Story { Title = $"Story {words}", Summary = $"Summary {words}", CreatedAt = now, UpdatedAt = now };
        story.Publish(now);
        story.Blocks.Add(new StoryBlock { Position = 0, Kind = StoryBlockKind.Text, Text = $"Text {words}" });
        story.Blocks.Add(new StoryBlock { Position = 1, Kind = StoryBlockKind.Item, ArchiveItem = item, Caption = $"Caption {words}" });
        db.AddRange(collection, item, story);
        await db.SaveChangesAsync();
        return new Seeded(story.Id, item.Id, story.Title, item.Title, words);
    });

    private static async Task<HttpClient> CuratorAsync(WebApplicationFactory<Program> archive)
    {
        var curator = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        return curator;
    }

    private static string EditPage(int id) => $"/Admin/Stories/Edit/{id}";

    private static Task<HttpResponseMessage> MoveToBinAsync(HttpClient curator, int id) =>
        Browser.PostFormAsync(curator, $"{EditPage(id)}?handler=MoveToBin", new(), EditPage(id));

    private static Task<HttpResponseMessage> BinPostAsync(HttpClient curator, string handler, int id) =>
        Browser.SendFormAsync(curator, $"/Admin/Bin?handler={handler}&id={id}", new(), "/Admin/Bin");

    private static async Task<string> TextAsync(HttpClient client, string address)
    {
        var response = await client.GetAsync(address);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : "";
    }

    private static Task<Story?> FindEvenInBinAsync(WebApplicationFactory<Program> archive, int id) =>
        archive.WithDbAsync(db => db.Stories.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == id));

    private static async Task<string> ExportJsonAsync(HttpClient curator, bool publishedOnly)
    {
        var fields = new Dictionary<string, string>();
        if (publishedOnly)
            fields["PublishedOnly"] = "true";
        var response = await Browser.PostFormAsync(curator, "/Admin/Export", fields);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        using var reader = new StreamReader(zip.GetEntry("archive.json")!.Open(), Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task A_story_in_the_bin_is_gone_for_visitors_and_curators_alike_until_it_is_restored()
    {
        await using var archive = await app.NewArchiveAsync();
        var seeded = await SeedAsync(archive);
        using var curator = await CuratorAsync(archive);
        using var visitor = archive.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/stories/{seeded.StoryId}")).StatusCode);
        Assert.Contains(seeded.StoryTitle, await TextAsync(visitor, "/sitemap.xml") + await TextAsync(visitor, "/stories"));

        var moved = await MoveToBinAsync(curator, seeded.StoryId);

        Assert.EndsWith("/Admin/Stories", moved.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/stories/{seeded.StoryId}")).StatusCode);
        Assert.DoesNotContain(seeded.StoryTitle, await TextAsync(visitor, "/stories"));
        Assert.DoesNotContain(seeded.StoryTitle, await TextAsync(visitor, "/"));
        Assert.DoesNotContain($"/stories/{seeded.StoryId}<", await TextAsync(visitor, "/sitemap.xml"));
        Assert.DoesNotContain(seeded.StoryTitle, await TextAsync(curator, "/Admin/Stories"));
        Assert.DoesNotContain(seeded.StoryTitle, await TextAsync(curator, $"/Admin/Items/Edit/{seeded.ItemId}"));
        var publishedExport = await ExportJsonAsync(curator, publishedOnly: true);
        Assert.DoesNotContain($"Text {seeded.Words}", publishedExport);
        Assert.DoesNotContain($"Caption {seeded.Words}", publishedExport);
        Assert.DoesNotContain(seeded.StoryTitle, publishedExport);
        var full = await ExportJsonAsync(curator, publishedOnly: false);
        Assert.Contains(seeded.ItemTitle, full);
        Assert.DoesNotContain(seeded.StoryTitle, full);
        Assert.DoesNotContain($"Text {seeded.Words}", full);
        Assert.DoesNotContain($"Caption {seeded.Words}", full);
        var bin = await TextAsync(curator, "/Admin/Bin");
        Assert.Contains(seeded.StoryTitle, bin);
        Assert.Contains("Deleted for good on", bin);
        Assert.NotNull((await FindEvenInBinAsync(archive, seeded.StoryId))!.DeletedAt);

        // The page of a story in the bin does not open for curators either; the bin is where it is dealt with.
        var opened = await curator.GetAsync(EditPage(seeded.StoryId));
        Assert.True(opened.StatusCode == HttpStatusCode.NotFound || opened.RequestMessage!.RequestUri!.AbsolutePath == "/Admin/Bin");
        Assert.Equal(HttpStatusCode.NotFound, (await curator.GetAsync($"/Admin/Stories/Preview/{seeded.StoryId}")).StatusCode);

        await BinPostAsync(curator, "RestoreStory", seeded.StoryId);

        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/stories/{seeded.StoryId}")).StatusCode);
        var restored = await visitor.GetStringAsync($"/stories/{seeded.StoryId}");
        Assert.Contains($"Caption {seeded.Words}", restored);
        Assert.DoesNotContain(seeded.StoryTitle, await TextAsync(curator, "/Admin/Bin"));
        Assert.Contains(seeded.StoryTitle, await TextAsync(curator, "/Admin/Stories"));
        Assert.Contains(seeded.StoryTitle, await TextAsync(curator, $"/Admin/Items/Edit/{seeded.ItemId}"));
        Assert.Equal(
            [AuditAction.StoryMovedToBin, AuditAction.StoryRestored],
            await archive.WithDbAsync(db => db.AuditEntries.Where(e => e.StoryId == seeded.StoryId).OrderBy(e => e.Id).Select(e => e.Action).ToListAsync()));
    }

    [Fact]
    public async Task Deleting_a_story_from_the_bin_removes_it_and_its_parts_but_not_its_items()
    {
        await using var archive = await app.NewArchiveAsync();
        var seeded = await SeedAsync(archive);
        using var curator = await CuratorAsync(archive);
        await MoveToBinAsync(curator, seeded.StoryId);

        await BinPostAsync(curator, "DeleteStory", seeded.StoryId);

        Assert.Null(await FindEvenInBinAsync(archive, seeded.StoryId));
        Assert.False(await archive.WithDbAsync(db => db.StoryBlocks.IgnoreQueryFilters().AnyAsync(b => b.StoryId == seeded.StoryId)));
        Assert.True(await archive.WithDbAsync(db => db.ArchiveItems.AnyAsync(i => i.Id == seeded.ItemId && i.Status == ItemStatus.Published)));
        // The log keeps what was done and the title, which is the curator's own wording; nothing links to a story that is gone.
        var entries = await archive.WithDbAsync(db => db.AuditEntries.Where(e => e.Subject == seeded.StoryTitle).OrderBy(e => e.Id).ToListAsync());
        Assert.Equal([AuditAction.StoryMovedToBin, AuditAction.StoryDeleted], entries.Select(e => e.Action));
        Assert.All(entries, e => Assert.Null(e.StoryId));
        Assert.Contains("Deleted the story for good", await TextAsync(curator, "/Admin/Activity"));
    }

    [Fact]
    public async Task Only_a_story_in_the_bin_can_be_restored_or_deleted_for_good()
    {
        await using var archive = await app.NewArchiveAsync();
        var seeded = await SeedAsync(archive);
        using var curator = await CuratorAsync(archive);

        Assert.Equal(HttpStatusCode.NotFound, (await BinPostAsync(curator, "DeleteStory", seeded.StoryId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await BinPostAsync(curator, "RestoreStory", seeded.StoryId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await BinPostAsync(curator, "DeleteStory", 987654)).StatusCode);

        var story = await FindEvenInBinAsync(archive, seeded.StoryId);
        Assert.Equal((StoryStatus.Published, null), (story!.Status, story.DeletedAt));
    }

    [Fact]
    public async Task A_story_stays_in_the_bin_for_30_days_and_is_then_deleted_by_the_purge_with_a_log_entry_without_a_person()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var archive = await app.NewArchiveAsync(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));
        var seeded = await SeedAsync(archive);
        var keep = await SeedAsync(archive);
        var inBinTooShort = await SeedAsync(archive);
        using (var curator = await CuratorAsync(archive))
        {
            await MoveToBinAsync(curator, seeded.StoryId);
            clock.Now = clock.Now.AddDays(10);
            await MoveToBinAsync(curator, inBinTooShort.StoryId);
        }

        async Task PurgeAsync()
        {
            using var scope = archive.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ArchiveBin>().PurgeExpiredAsync();
        }

        // 29 days after the first story went to the bin: nothing is deleted yet.
        clock.Now = clock.Now.AddDays(19);
        await PurgeAsync();
        Assert.NotNull(await FindEvenInBinAsync(archive, seeded.StoryId));

        // Just over 30 days after: the first story is gone, the second (10 days younger) and a story never in the bin stay.
        clock.Now = clock.Now.AddDays(1).AddHours(1);
        await PurgeAsync();

        Assert.Null(await FindEvenInBinAsync(archive, seeded.StoryId));
        Assert.False(await archive.WithDbAsync(db => db.StoryBlocks.IgnoreQueryFilters().AnyAsync(b => b.StoryId == seeded.StoryId)));
        Assert.NotNull(await FindEvenInBinAsync(archive, inBinTooShort.StoryId));
        Assert.NotNull(await FindEvenInBinAsync(archive, keep.StoryId));
        Assert.True(await archive.WithDbAsync(db => db.ArchiveItems.AnyAsync(i => i.Id == seeded.ItemId && i.Status == ItemStatus.Published)));
        var purged = await archive.WithDbAsync(db => db.AuditEntries.SingleAsync(e => e.Action == AuditAction.StoryPurged));
        Assert.Equal((seeded.StoryTitle, null, null, null), (purged.Subject, purged.UserName, purged.UserId, purged.StoryId));

        // A day later the second one follows.
        clock.Now = clock.Now.AddDays(10);
        await PurgeAsync();
        Assert.Null(await FindEvenInBinAsync(archive, inBinTooShort.StoryId));
        Assert.NotNull(await FindEvenInBinAsync(archive, keep.StoryId));
    }

    [Fact]
    public async Task The_page_of_an_item_names_its_stories_for_curators_and_not_for_helpers()
    {
        var seeded = await app.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var words = TestData.UniqueWord("used");
            var item = new ArchiveItem { Title = $"Used {words}", Collection = new Collection { Title = $"Box {words}", CreatedAt = now }, CreatedAt = now, UpdatedAt = now };
            Story Make(string title, bool published, bool binned)
            {
                var story = new Story { Title = title, CreatedAt = now, UpdatedAt = now };
                if (published)
                    story.Publish(now);
                story.Blocks.Add(new StoryBlock { Position = 0, Kind = StoryBlockKind.Item, ArchiveItem = item });
                if (binned)
                    story.MoveToBin(now);
                return story;
            }
            var first = Make($"First {words}", published: true, binned: false);
            var second = Make($"Second {words}", published: false, binned: false);
            var third = Make($"Third {words}", published: true, binned: true);
            db.AddRange(item, first, second, third);
            await db.SaveChangesAsync();
            return (Item: item.Id, Words: words, First: first.Id, Second: second.Id);
        });
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (helper, _, _, _) = await app.CreateAccountAsync(admin, Roles.Helper);
        using (helper)
        {
            var page = $"/Admin/Items/Edit/{seeded.Item}";

            var forCurator = await admin.GetStringAsync(page);

            Assert.Contains("Used in 2 stories", forCurator);
            Assert.Contains($"href=\"{EditPage(seeded.First)}\"", forCurator);
            Assert.Contains($"First {seeded.Words}", forCurator);
            Assert.Contains($"Second {seeded.Words}", forCurator);
            Assert.DoesNotContain($"Third {seeded.Words}", forCurator);
            Assert.Contains("visitors do not see it in these stories yet", forCurator);

            var forHelper = await helper.GetStringAsync(page);

            Assert.DoesNotContain("Used in", forHelper);
            Assert.DoesNotContain($"First {seeded.Words}", forHelper);
            Assert.DoesNotContain(EditPage(seeded.First), forHelper);

            // Public: the note says that unpublishing or binning takes it out of the stories.
            await app.WithDbAsync(async db =>
            {
                (await db.ArchiveItems.SingleAsync(i => i.Id == seeded.Item)).Publish(DateTime.UtcNow);
                return await db.SaveChangesAsync();
            });
            var published = await admin.GetStringAsync(page);
            Assert.Contains("it leaves the published stories", published);
            Assert.DoesNotContain("visitors do not see it in these stories yet", published);
        }
    }

    [Fact]
    public async Task The_edit_handlers_refuse_a_story_in_the_bin()
    {
        await using var archive = await app.NewArchiveAsync();
        var seeded = await SeedAsync(archive);
        using var curator = await CuratorAsync(archive);
        var page = EditPage(seeded.StoryId);
        await MoveToBinAsync(curator, seeded.StoryId);

        foreach (var handler in new[] { "AddText", "Publish", "Unpublish", "MoveToBin" })
            Assert.Equal(HttpStatusCode.NotFound, (await Browser.SendFormAsync(curator, $"{page}?handler={handler}", new() { ["text"] = "Late words" }, "/Admin/Stories")).StatusCode);

        var story = await FindEvenInBinAsync(archive, seeded.StoryId);
        Assert.NotNull(story!.DeletedAt);
        Assert.False(await archive.WithDbAsync(db => db.StoryBlocks.IgnoreQueryFilters().AnyAsync(b => b.StoryId == seeded.StoryId && b.Text == "Late words")));
    }

    [Fact]
    public async Task A_helper_gets_nothing_of_the_bin_of_stories()
    {
        var seeded = await app.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var story = new Story { Title = TestData.Unique("Helper bin"), CreatedAt = now, UpdatedAt = now };
            story.MoveToBin(now);
            db.Stories.Add(story);
            await db.SaveChangesAsync();
            return story.Id;
        });
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (helper, _, _, _) = await app.CreateAccountAsync(admin, Roles.Helper);
        using (helper)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await helper.GetAsync("/Admin/Bin")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await helper.GetAsync(EditPage(seeded))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Browser.SendFormAsync(helper, $"/Admin/Bin?handler=DeleteStory&id={seeded}", new(), "/Admin")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Browser.SendFormAsync(helper, $"{EditPage(seeded)}?handler=MoveToBin", new(), "/Admin")).StatusCode);
        }

        Assert.NotNull(await app.WithDbAsync(db => db.Stories.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == seeded)));
    }
}
