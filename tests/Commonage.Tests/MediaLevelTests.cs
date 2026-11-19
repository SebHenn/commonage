using System.IO.Compression;
using System.Text.Json;
using Commonage.Web.Domain;
using Commonage.Web.Identity;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

/// <summary>Who may choose what visitors get of an item, what is recorded, and what the copies of the archive hold.</summary>
[Collection(nameof(AppCollection))]
public class MediaLevelTests(CommonageAppFactory app)
{
    private async Task<(string Word, int ItemId, int CollectionId, int FileId)> SeedAsync(ItemStatus status = ItemStatus.Published)
    {
        var word = TestData.UniqueWord("level");
        return await app.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = $"Box {word}", CreatedAt = now };
            var item = new ArchiveItem { Title = $"Title {word}", Collection = collection, CreatedAt = now, UpdatedAt = now };
            var file = new MediaFile
            {
                ArchiveItem = item, OriginalFileName = "a.pdf", ContentType = "application/pdf", Sha256 = new string('b', 64),
                StorageId = Guid.NewGuid(), Status = ProcessingStatus.Ready, Caption = $"Caption {word}", ExtractedText = $"Pages {word}",
            };
            item.MediaFiles.Add(file);
            item.FileText = new ItemFileText { Text = $"Pages {word}" };
            if (status == ItemStatus.Published)
                item.Publish(now);
            db.AddRange(collection, item);
            await db.SaveChangesAsync();
            return (word, item.Id, collection.Id, file.Id);
        });
    }

    private Task<MediaLevel> LevelOfAsync(int itemId) => app.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == itemId).Select(i => i.MediaLevel).SingleAsync());

    [Fact]
    public async Task A_curator_changes_what_visitors_get_and_it_is_recorded_while_a_helper_cannot()
    {
        // A draft, as a helper cannot open a published item at all.
        var seeded = await SeedAsync(ItemStatus.Draft);
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (helper, _, _, _) = await app.CreateAccountAsync(admin, Roles.Helper);
        var page = $"/Admin/Items/Edit/{seeded.ItemId}";

        await Browser.SendFormAsync(helper, $"/Admin/Items/Edit/{seeded.ItemId}", new()
        {
            ["Input.Title"] = $"Title {seeded.Word}", ["Input.CollectionId"] = seeded.CollectionId.ToString(), ["Input.MediaLevel"] = nameof(MediaLevel.RecordOnly),
        }, page);
        var afterHelper = await LevelOfAsync(seeded.ItemId);

        await Browser.SendFormAsync(admin, page, new()
        {
            ["Input.Title"] = $"Title {seeded.Word}", ["Input.CollectionId"] = seeded.CollectionId.ToString(), ["Input.MediaLevel"] = nameof(MediaLevel.RecordOnly),
        });
        var afterCurator = await LevelOfAsync(seeded.ItemId);
        var recorded = await app.WithDbAsync(db => db.AuditEntries.AnyAsync(e => e.Action == AuditAction.ItemMediaLevelChanged && e.ArchiveItemId == seeded.ItemId));

        Assert.Equal(MediaLevel.Everything, afterHelper);
        Assert.Equal(MediaLevel.RecordOnly, afterCurator);
        Assert.True(recorded);
    }

    [Fact]
    public async Task A_curator_still_finds_an_item_by_what_its_pdf_says_when_visitors_cannot()
    {
        var seeded = await SeedAsync();
        await app.WithDbAsync(async db =>
        {
            (await db.ArchiveItems.SingleAsync(i => i.Id == seeded.ItemId)).MediaLevel = MediaLevel.RecordOnly;
            await db.SaveChangesAsync();
            return true;
        });
        using var visitor = app.CreateClient();
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);

        Assert.DoesNotContain($"Title {seeded.Word}", await visitor.GetStringAsync($"/search?q=Pages+{seeded.Word}"));
        Assert.Contains($"Title {seeded.Word}", await curator.GetStringAsync($"/Admin?q=Pages+{seeded.Word}"));
    }

    [Fact]
    public async Task The_public_copy_of_the_export_holds_no_file_of_a_record_only_item_but_the_full_export_does()
    {
        var seeded = await SeedAsync();
        await app.WithDbAsync(async db =>
        {
            (await db.ArchiveItems.SingleAsync(i => i.Id == seeded.ItemId)).MediaLevel = MediaLevel.RecordOnly;
            await db.SaveChangesAsync();
            return true;
        });
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);

        var published = await ItemJsonAsync(admin, seeded.ItemId, publishedOnly: true);
        var full = await ItemJsonAsync(admin, seeded.ItemId, publishedOnly: false);

        Assert.Equal("RecordOnly", published.GetProperty("mediaLevel").GetString());
        Assert.Empty(published.GetProperty("files").EnumerateArray());
        Assert.Single(full.GetProperty("files").EnumerateArray());
    }

    private static async Task<JsonElement> ItemJsonAsync(HttpClient admin, int itemId, bool publishedOnly)
    {
        var response = await Browser.PostFormAsync(admin, "/Admin/Export", new() { ["PublishedOnly"] = publishedOnly ? "true" : "false" });
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var zip = new ZipArchive(stream);
        using var reader = new StreamReader(zip.GetEntry("archive.json")!.Open());
        var items = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.GetProperty("items");
        return items.EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == itemId).Clone();
    }
}
