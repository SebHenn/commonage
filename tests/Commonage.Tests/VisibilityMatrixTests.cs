using System.Net;
using System.Text;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
        var shown = await SeedAsync("noteshown", (i, now) => i.Publish(now));
        var draft = await SeedAsync("notedraft", (_, _) => { });
        var embargoed = await SeedAsync("noteembargoed", (i, now) => { i.Publish(now); i.SetPublicFrom(DateOnly.FromDateTime(now).AddDays(3), now); });
        using var visitor = app.CreateClient();

        await WithVisitorNotesOnAsync(async () =>
        {
            foreach (var hidden in new[] { draft, embargoed })
                Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/items/{hidden.ItemId}/tell")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/items/{shown.ItemId}/tell")).StatusCode);
        });
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

    [Fact]
    public async Task Hidden_items_beside_a_shown_one_do_not_appear_as_its_neighbours_or_under_a_shared_tag()
    {
        var word = TestData.UniqueWord("neighbours");
        var (shownId, collectionId, hiddenWords) = await app.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = $"Shared {word}", CreatedAt = now };
            var tag = new Tag { Name = $"T {word}", NormalizedName = $"t {word}", Kind = TagKind.Topic };
            ArchiveItem Make(string kind, Action<ArchiveItem> state)
            {
                var item = new ArchiveItem { Title = $"{kind} {word}", Collection = collection, CreatedAt = now, UpdatedAt = now };
                item.TrySetDate("1954");
                item.SetTags([tag]);
                state(item);
                return item;
            }
            var shown = Make("Shown", i => i.Publish(now));
            var hidden = new[]
            {
                Make("Draftneighbour", _ => { }),
                Make("Embargoneighbour", i => { i.Publish(now); i.SetPublicFrom(DateOnly.FromDateTime(now).AddDays(3), now); }),
                Make("Binneighbour", i => { i.Publish(now); i.MoveToBin(now); }),
            };
            db.AddRange(collection, shown);
            db.AddRange(hidden);
            await db.SaveChangesAsync();
            return (shown.Id, collection.Id, hidden.Select(h => h.Title).ToArray());
        });
        using var visitor = app.CreateClient();

        var text = string.Concat(
            await TextAsync(visitor, $"/items/{shownId}"),
            await TextAsync(visitor, $"/collections/{collectionId}"),
            await TextAsync(visitor, $"/search?q={word}"),
            await TextAsync(visitor, "/search?q=1954"));

        Assert.Contains($"Shown {word}", text);
        foreach (var title in hiddenWords)
            Assert.DoesNotContain(title, text);
    }

    [Fact]
    public async Task A_visitor_cannot_send_a_note_about_a_hidden_item()
    {
        var shown = await SeedAsync("sendshown", (i, now) => i.Publish(now));
        var draft = await SeedAsync("senddraft", (_, _) => { });
        var embargoed = await SeedAsync("sendembargoed", (i, now) => { i.Publish(now); i.SetPublicFrom(DateOnly.FromDateTime(now).AddDays(3), now); });
        using var visitor = app.CreateClient();

        await WithVisitorNotesOnAsync(async () =>
        {
            foreach (var hidden in new[] { draft, embargoed })
            {
                var response = await Browser.SendFormAsync(visitor, $"/items/{hidden.ItemId}/tell",
                    new() { ["Input.Kind"] = "Information", ["Input.Message"] = $"Note {hidden.Word}" }, $"/items/{shown.ItemId}/tell");
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        });

        Assert.False(await app.WithDbAsync(db => db.VisitorNotes.AnyAsync(n => n.ArchiveItemId == draft.ItemId || n.ArchiveItemId == embargoed.ItemId)));
    }

    [Fact]
    public async Task The_recordings_and_pdf_pages_of_hidden_and_record_only_items_are_not_served_to_visitors()
    {
        var storage = app.Services.GetRequiredService<IFileStorage>();
        async Task<(int Audio, int Pdf)> SeedFilesAsync(string kind, Action<ArchiveItem, DateTime> state)
        {
            var marked = await SeedAsync(kind, state);
            return await app.WithDbAsync(async db =>
            {
                var item = await db.ArchiveItems.IgnoreQueryFilters().SingleAsync(i => i.Id == marked.ItemId);
                var audio = new MediaFile
                {
                    ArchiveItemId = item.Id, OriginalFileName = "a.mp3", ContentType = "audio/mpeg", Sha256 = new string('b', 64),
                    StorageId = Guid.NewGuid(), Status = ProcessingStatus.Ready, UploadedAt = DateTime.UtcNow,
                };
                var pdf = new MediaFile
                {
                    ArchiveItemId = item.Id, OriginalFileName = "a.pdf", ContentType = "application/pdf", Sha256 = new string('c', 64),
                    StorageId = Guid.NewGuid(), Status = ProcessingStatus.Ready, PageCount = 1, UploadedAt = DateTime.UtcNow,
                };
                db.AddRange(audio, pdf);
                await db.SaveChangesAsync();
                await storage.SaveAsync(audio.AudioKey, new MemoryStream([1, 2, 3]));
                await storage.SaveAsync(pdf.PageKey(1), new MemoryStream([1, 2, 3]));
                return (audio.Id, pdf.Id);
            });
        }
        var shown = await SeedFilesAsync("filesshown", (i, now) => i.Publish(now));
        var hidden = new[]
        {
            await SeedFilesAsync("filesdraft", (_, _) => { }),
            await SeedFilesAsync("filesbinned", (i, now) => { i.Publish(now); i.MoveToBin(now); }),
            await SeedFilesAsync("filesembargoed", (i, now) => { i.Publish(now); i.SetPublicFrom(DateOnly.FromDateTime(now).AddDays(3), now); }),
            await SeedFilesAsync("filesrecord", (i, now) => { i.Publish(now); i.MediaLevel = MediaLevel.RecordOnly; }),
        };
        using var visitor = app.CreateClient();

        // The shown item proves the addresses work, so the refusals below are about visibility and not missing files.
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/media/{shown.Audio}/audio")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/media/{shown.Pdf}/page/1")).StatusCode);
        foreach (var files in hidden)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{files.Audio}/audio")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{files.Pdf}/page/1")).StatusCode);
        }
    }

    [Fact]
    public async Task The_decade_timeline_does_not_count_a_hidden_item_with_a_date()
    {
        var word = TestData.UniqueWord("decades");
        await app.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = $"Box {word}", CreatedAt = now };
            ArchiveItem Make(string date, Action<ArchiveItem> state)
            {
                var item = new ArchiveItem { Title = $"Dated {word}", Collection = collection, CreatedAt = now, UpdatedAt = now };
                item.TrySetDate(date);
                state(item);
                return item;
            }
            db.AddRange(collection,
                Make("1930", i => i.Publish(now)),
                Make("1950", i => i.Publish(now)),
                Make("1810", _ => { }),
                Make("1820", i => { i.Publish(now); i.SetPublicFrom(DateOnly.FromDateTime(now).AddDays(3), now); }),
                Make("1830", i => { i.Publish(now); i.MoveToBin(now); }));
            await db.SaveChangesAsync();
            return 0;
        });
        using var visitor = app.CreateClient();

        var text = await TextAsync(visitor, $"/search?q={word}");

        Assert.Contains("1930", text);
        foreach (var decade in new[] { "1810", "1820", "1830" })
            Assert.DoesNotContain(decade, text);
    }

    private async Task WithVisitorNotesOnAsync(Func<Task> body)
    {
        async Task<int> Set(bool on) => await app.WithDbAsync(async db =>
        {
            var settings = await db.ArchiveSettings.FirstOrDefaultAsync();
            if (settings is null)
                db.ArchiveSettings.Add(settings = new ArchiveSettings());
            settings.AcceptVisitorNotes = on;
            var saved = await db.SaveChangesAsync();
            app.Services.GetRequiredService<Commonage.Web.Operations.ArchiveProfile>().Forget();
            return saved;
        });
        await Set(true);
        try { await body(); }
        finally { await Set(false); }
    }

    private static async Task<string> TextAsync(HttpClient client, string address)
    {
        var response = await client.GetAsync(address);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : "";
    }
}
