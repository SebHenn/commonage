using System.Net;
using System.Text;
using Commonage.Web.Data;
using Commonage.Web.Domain;

namespace Commonage.Tests;

/// <summary>
/// What the public may see, address by address. Every item has a word of its own in its title, description, tag, credit,
/// caption and the text of its PDF, so a page that shows any part of a hidden item is caught, and not only its title.
/// A new public address that is about items goes into <see cref="Addresses"/>.
/// </summary>
[Collection(nameof(AppCollection))]
public class VisibilityMatrixTests(CommonageAppFactory app)
{
    private sealed record Marked(string Word, int ItemId, int CollectionId, int FileId);

    private static readonly string[] Addresses =
    [
        "/", "/search?q={0}", "/search", "/collections", "/places", "/people", "/help", "/feed.xml", "/sitemap.xml",
    ];

    private async Task<Marked> SeedAsync(string kind, Action<ArchiveItem, DateTime> state)
    {
        var word = TestData.UniqueWord(kind);
        return await app.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = $"Box {word}", CreatedAt = now };
            var item = new ArchiveItem
            {
                Title = $"Title {word}", Description = $"Words {word}", Collection = collection, CreatedAt = now, UpdatedAt = now,
                Credit = $"Credit {word}",
            };
            item.SetTags([new Tag { Name = $"P {word}", NormalizedName = $"p {word}", Kind = TagKind.Place }]);
            var file = new MediaFile
            {
                ArchiveItem = item, OriginalFileName = "a.jpg", ContentType = "image/jpeg", Sha256 = new string('a', 64),
                StorageId = Guid.NewGuid(), Status = ProcessingStatus.Ready, AltText = $"Alt {word}", Caption = $"Caption {word}",
                ExtractedText = $"Pages {word}",
            };
            item.MediaFiles.Add(file);
            item.FileText = new ItemFileText { Text = $"Pages {word}" };
            state(item, now);
            db.AddRange(collection, item);
            await db.SaveChangesAsync();
            return new Marked(word, item.Id, collection.Id, file.Id);
        });
    }

    [Fact]
    public async Task Only_published_items_show_up_anywhere_a_visitor_can_look()
    {
        var shown = await SeedAsync("shown", (i, now) => i.Publish(now));
        var draft = await SeedAsync("draft", (_, _) => { });
        var binned = await SeedAsync("binned", (i, now) => { i.Publish(now); i.MoveToBin(now); });
        var embargoed = await SeedAsync("embargoed", (i, now) => { i.Publish(now); i.SetPublicFrom(DateOnly.FromDateTime(now).AddDays(3), now); });
        var opened = await SeedAsync("opened", (i, now) => { i.Publish(now); i.SetPublicFrom(DateOnly.FromDateTime(now).AddDays(-3), now); });
        using var visitor = app.CreateClient();

        var everything = new StringBuilder();
        foreach (var marked in new[] { shown, opened, draft, binned, embargoed })
        {
            foreach (var address in Addresses)
                everything.Append(await TextAsync(visitor, string.Format(address, marked.Word)));
            everything.Append(await TextAsync(visitor, $"/items/{marked.ItemId}"));
            everything.Append(await TextAsync(visitor, $"/collections/{marked.CollectionId}"));
        }
        var text = everything.ToString();

        Assert.Contains($"Title {shown.Word}", text);
        Assert.Contains($"Title {opened.Word}", text);
        foreach (var hidden in new[] { draft, binned, embargoed })
            foreach (var part in new[] { "Title ", "Words ", "Credit ", "Alt ", "Caption ", "Pages ", "Box ", "P " })
                Assert.DoesNotContain(part + hidden.Word, text);
    }

    [Fact]
    public async Task A_collection_of_hidden_items_has_no_page_card_or_sitemap_entry()
    {
        var draft = await SeedAsync("onlydraft", (_, _) => { });
        var embargoed = await SeedAsync("onlyembargoed", (i, now) => { i.Publish(now); i.SetPublicFrom(DateOnly.FromDateTime(now).AddDays(3), now); });
        using var visitor = app.CreateClient();

        foreach (var hidden in new[] { draft, embargoed })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/collections/{hidden.CollectionId}")).StatusCode);
            Assert.DoesNotContain($"/collections/{hidden.CollectionId}\"", await TextAsync(visitor, "/collections"));
            Assert.DoesNotContain($"/collections/{hidden.CollectionId}<", await TextAsync(visitor, "/sitemap.xml"));
        }
    }

    [Fact]
    public async Task The_pictures_of_hidden_items_are_not_served_to_visitors()
    {
        var draft = await SeedAsync("picturedraft", (_, _) => { });
        var binned = await SeedAsync("picturebinned", (i, now) => { i.Publish(now); i.MoveToBin(now); });
        var embargoed = await SeedAsync("pictureembargoed", (i, now) => { i.Publish(now); i.SetPublicFrom(DateOnly.FromDateTime(now).AddDays(3), now); });
        using var visitor = app.CreateClient();

        foreach (var hidden in new[] { draft, binned, embargoed })
            foreach (var variant in new[] { "preview", "thumbnail", "card", "large" })
                Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{hidden.FileId}/{variant}")).StatusCode);
    }

    [Fact]
    public async Task A_visitor_cannot_open_the_note_form_of_a_hidden_item()
    {
        var draft = await SeedAsync("notedraft", (_, _) => { });
        var embargoed = await SeedAsync("noteembargoed", (i, now) => { i.Publish(now); i.SetPublicFrom(DateOnly.FromDateTime(now).AddDays(3), now); });
        using var visitor = app.CreateClient();

        foreach (var hidden in new[] { draft, embargoed })
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/items/{hidden.ItemId}/tell")).StatusCode);
    }

    [Fact]
    public async Task A_record_only_item_shows_its_record_and_nothing_about_its_media()
    {
        var record = await SeedAsync("recordonly", (i, now) => { i.Publish(now); i.MediaLevel = MediaLevel.RecordOnly; });
        using var visitor = app.CreateClient();

        var everything = new StringBuilder();
        foreach (var address in Addresses)
            everything.Append(await TextAsync(visitor, string.Format(address, record.Word)));
        everything.Append(await TextAsync(visitor, $"/items/{record.ItemId}"));
        everything.Append(await TextAsync(visitor, $"/collections/{record.CollectionId}"));
        var text = everything.ToString();

        foreach (var part in new[] { "Title ", "Words ", "Credit ", "P " })
            Assert.Contains(part + record.Word, text);
        foreach (var part in new[] { "Alt ", "Caption ", "Pages " })
            Assert.DoesNotContain(part + record.Word, text);
        Assert.DoesNotContain($"/media/{record.FileId}/", text);
        foreach (var variant in new[] { "preview", "thumbnail", "card", "large" })
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{record.FileId}/{variant}")).StatusCode);

        // What the pages of the PDFs say is not found by a visitor, but a curator finds it.
        Assert.DoesNotContain($"Title {record.Word}", await TextAsync(visitor, $"/search?q=Pages+{record.Word}"));
    }

    private static async Task<string> TextAsync(HttpClient client, string address)
    {
        var response = await client.GetAsync(address);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : "";
    }
}
