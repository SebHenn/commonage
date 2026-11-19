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
        using var visitor = app.CreateClient();

        var everything = new StringBuilder();
        foreach (var marked in new[] { shown, draft, binned })
        {
            foreach (var address in Addresses)
                everything.Append(await TextAsync(visitor, string.Format(address, marked.Word)));
            everything.Append(await TextAsync(visitor, $"/items/{marked.ItemId}"));
            everything.Append(await TextAsync(visitor, $"/collections/{marked.CollectionId}"));
        }
        var text = everything.ToString();

        Assert.Contains($"Title {shown.Word}", text);
        foreach (var hidden in new[] { draft, binned })
            foreach (var part in new[] { "Title ", "Words ", "Credit ", "Alt ", "Caption ", "Pages ", "Box ", "P " })
                Assert.DoesNotContain(part + hidden.Word, text);
    }

    [Fact]
    public async Task A_collection_of_hidden_items_has_no_page_card_or_sitemap_entry()
    {
        var draft = await SeedAsync("onlydraft", (_, _) => { });
        using var visitor = app.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/collections/{draft.CollectionId}")).StatusCode);
        Assert.DoesNotContain($"/collections/{draft.CollectionId}\"", await TextAsync(visitor, "/collections"));
        Assert.DoesNotContain($"/collections/{draft.CollectionId}<", await TextAsync(visitor, "/sitemap.xml"));
    }

    [Fact]
    public async Task The_pictures_of_hidden_items_are_not_served_to_visitors()
    {
        var draft = await SeedAsync("picturedraft", (_, _) => { });
        var binned = await SeedAsync("picturebinned", (i, now) => { i.Publish(now); i.MoveToBin(now); });
        using var visitor = app.CreateClient();

        foreach (var hidden in new[] { draft, binned })
            foreach (var variant in new[] { "preview", "thumbnail", "card", "large" })
                Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{hidden.FileId}/{variant}")).StatusCode);
    }

    [Fact]
    public async Task A_visitor_cannot_open_the_note_form_of_a_hidden_item()
    {
        var draft = await SeedAsync("notedraft", (_, _) => { });
        using var visitor = app.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/items/{draft.ItemId}/tell")).StatusCode);
    }

    private static async Task<string> TextAsync(HttpClient client, string address)
    {
        var response = await client.GetAsync(address);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : "";
    }
}
