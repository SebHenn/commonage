using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

/// <summary>Collections as curators arrange them: their order, the order of their items, a cover, and deleting an empty one.</summary>
[Collection(nameof(AppCollection))]
public partial class CollectionTests(CommonageAppFactory app)
{
    /// <summary>
    /// Three collections, in an order their titles would not give. "Streets" has dated items in an order that differs by every
    /// sort: two on the same day, two without a date, one open-ended, and a draft and an item in the bin that must not count.
    /// </summary>
    private static async Task<WebApplicationFactory<Program>> ArchiveAsync(CommonageAppFactory app)
    {
        var archive = await app.NewArchiveAsync();
        await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var streets = new Collection { Title = "Streets", CreatedAt = now };
            var allotments = new Collection { Title = "Allotments", CreatedAt = now };
            var harbour = new Collection { Title = "Harbour", CreatedAt = now };
            var minutes = 0;
            ArchiveItem Item(Collection collection, string title, string? date, ItemStatus status = ItemStatus.Published)
            {
                var item = new ArchiveItem { Title = title, Collection = collection, CreatedAt = now, UpdatedAt = now, Status = ItemStatus.Draft };
                Assert.True(item.TrySetDate(date));
                if (status == ItemStatus.Published)
                    // Published one minute apart, in the order they are listed here.
                    item.Publish(now.AddMinutes(minutes++));
                return item;
            }

            var binned = Item(streets, "Torn down", "1890");
            binned.MoveToBin(now);
            db.ArchiveItems.AddRange(
                Item(streets, "Mill Street flood", "1950"),
                Item(streets, "Tram depot", "1930"),
                Item(streets, "Undated corner", null),
                Item(streets, "Market day", "1965"),
                Item(streets, "Before the war", "before 1945"),
                Item(streets, "Second undated", null),
                Item(streets, "Same day as the flood", "1950"),
                Item(streets, "Draft from 1900", "1900", ItemStatus.Draft),
                binned,
                Item(allotments, "Sheds", "1953"),
                Item(harbour, "Ferry", null));
            return await db.SaveChangesAsync();
        });
        return archive;
    }

    private static Task<int> CollectionIdAsync(WebApplicationFactory<Program> archive, string title) =>
        archive.WithDbAsync(db => db.Collections.Where(c => c.Title == title).Select(c => c.Id).SingleAsync());

    private static async Task<HttpClient> CuratorAsync(WebApplicationFactory<Program> archive)
    {
        var curator = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        return curator;
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient curator, int id, string title, string sort, int? cover = null) =>
        Browser.SendFormAsync(curator, $"/Admin/Collections/Edit/{id}", new()
        {
            ["Input.Title"] = title,
            ["Input.Sort"] = sort,
            ["Input.CoverMediaFileId"] = cover?.ToString() ?? "",
        });

    /// <summary>The titles of the collections on a page of cards, in the order shown.</summary>
    private static List<string> CardTitles(string page) =>
        CardTitle().Matches(page).Select(m => m.Groups[1].Value).ToList();

    [GeneratedRegex("""class="collection-card-title"[^>]*>([^<]+)<""")]
    private static partial Regex CardTitle();

    [GeneratedRegex("""<a (?=[^>]*class="item-card")[^>]*href="/items/(?<id>\d+)""")]
    private static partial Regex ItemCardLink();

    [GeneratedRegex("""<a (?=[^>]*rel="(?<rel>prev|next)")[^>]*href="/items/(?<id>\d+)""")]
    private static partial Regex NeighbourLink();

    [Fact]
    public async Task Visitors_find_the_collections_in_the_order_curators_give_them()
    {
        await using var archive = await ArchiveAsync(app);
        using var visitor = archive.CreateClient();
        using var curator = await CuratorAsync(archive);
        var streets = await CollectionIdAsync(archive, "Streets");

        // Before anyone sets an order, they go by title, as they did before there was one.
        Assert.Equal(["Allotments", "Harbour", "Streets"], CardTitles(await visitor.GetStringAsync("/collections")));

        await Browser.PostFormAsync(curator, $"/Admin/Collections?handler=MoveEarlier&id={streets}", new(), "/Admin/Collections");
        Assert.Equal(["Allotments", "Streets", "Harbour"], CardTitles(await visitor.GetStringAsync("/collections")));
        await Browser.PostFormAsync(curator, $"/Admin/Collections?handler=MoveEarlier&id={streets}", new(), "/Admin/Collections");
        // The first stays first.
        await Browser.PostFormAsync(curator, $"/Admin/Collections?handler=MoveEarlier&id={streets}", new(), "/Admin/Collections");
        Assert.Equal(["Streets", "Allotments", "Harbour"], CardTitles(await visitor.GetStringAsync("/collections")));
        Assert.Equal(["Streets", "Allotments", "Harbour"], CardTitles(await visitor.GetStringAsync("/")));

        var harbour = await CollectionIdAsync(archive, "Harbour");
        await Browser.PostFormAsync(curator, $"/Admin/Collections?handler=MoveLater&id={harbour}", new(), "/Admin/Collections");
        await Browser.PostFormAsync(curator, $"/Admin/Collections?handler=MoveLater&id={streets}", new(), "/Admin/Collections");
        Assert.Equal(["Allotments", "Streets", "Harbour"], CardTitles(await visitor.GetStringAsync("/collections")));

        // A new collection comes last, whatever its title.
        await Browser.PostFormAsync(curator, "/Admin/Collections/Edit", new() { ["Input.Title"] = "Aardvark club", ["Input.Sort"] = "RecentlyAdded" });
        var list = await curator.GetStringAsync("/Admin/Collections");
        Assert.True(list.IndexOf("Harbour", StringComparison.Ordinal) < list.IndexOf("Aardvark club", StringComparison.Ordinal));

        var log = await curator.GetStringAsync("/Admin/Activity?what=Collections");
        Assert.Contains("Changed the order of the collections", log);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Browser.SendFormAsync(curator, "/Admin/Collections?handler=MoveEarlier&id=999999", new(), "/Admin/Collections")).StatusCode);
    }

    [Theory]
    [InlineData("RecentlyAdded", "Same day as the flood|Second undated|Before the war|Market day|Undated corner|Tram depot|Mill Street flood")]
    [InlineData("OldestFirst", "Tram depot|Before the war|Mill Street flood|Same day as the flood|Market day|Undated corner|Second undated")]
    [InlineData("NewestFirst", "Market day|Same day as the flood|Mill Street flood|Before the war|Tram depot|Second undated|Undated corner")]
    public async Task A_collection_shows_its_items_in_its_order_and_previous_and_next_follow_it(string sort, string expected)
    {
        await using var archive = await ArchiveAsync(app);
        using var visitor = archive.CreateClient();
        using var curator = await CuratorAsync(archive);
        var streets = await CollectionIdAsync(archive, "Streets");
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(curator, streets, "Streets", sort)).StatusCode);

        var page = await visitor.GetStringAsync($"/collections/{streets}");
        var ids = ItemCardLink().Matches(page).Select(m => int.Parse(m.Groups["id"].Value)).ToList();
        var titles = await archive.WithDbAsync(db => db.ArchiveItems.ToDictionaryAsync(i => i.Id, i => i.Title));
        Assert.Equal(expected, string.Join('|', ids.Select(id => titles[id])));

        // Every item's "previous" and "next" are its neighbours on the collection's page, and the ends have none.
        for (var at = 0; at < ids.Count; at++)
        {
            var links = NeighbourLink().Matches(await visitor.GetStringAsync($"/items/{ids[at]}"))
                .ToDictionary(m => m.Groups["rel"].Value, m => int.Parse(m.Groups["id"].Value));
            Assert.Equal<int?>(at > 0 ? ids[at - 1] : null, links.TryGetValue("prev", out var previous) ? previous : null);
            Assert.Equal<int?>(at < ids.Count - 1 ? ids[at + 1] : null, links.TryGetValue("next", out var next) ? next : null);
        }
    }

    [Fact]
    public async Task A_collection_says_which_years_its_published_items_are_from()
    {
        await using var archive = await ArchiveAsync(app);
        using var visitor = archive.CreateClient();

        // The draft from 1900 and the item in the bin from 1890 do not count; "before 1945" counts with its end.
        Assert.Contains(">1930–1965<", await visitor.GetStringAsync($"/collections/{await CollectionIdAsync(archive, "Streets")}"));
        var cards = await visitor.GetStringAsync("/collections");
        Assert.Contains(">1930–1965<", cards);
        Assert.Contains(">1953<", cards);
        Assert.DoesNotContain("1900", cards);
        // A collection without dated items says nothing about years.
        Assert.DoesNotMatch(@">\d{4}(–\d{4})?<", await visitor.GetStringAsync($"/collections/{await CollectionIdAsync(archive, "Harbour")}"));
    }

    [Fact]
    public async Task Visitors_see_the_chosen_cover_only_while_its_item_is_published_and_in_the_collection()
    {
        await using var archive = await ArchiveAsync(app);
        using var visitor = archive.CreateClient();
        using var curator = await CuratorAsync(archive);
        var streets = await CollectionIdAsync(archive, "Streets");
        var harbour = await CollectionIdAsync(archive, "Harbour");
        var photos = await archive.WithDbAsync(async db =>
        {
            MediaFile Photo(string title) => new()
            {
                OriginalFileName = "photo.jpg",
                ContentType = "image/jpeg",
                Sha256 = new string('0', 64),
                StorageId = Guid.NewGuid(),
                UploadedAt = DateTime.UtcNow,
                Status = ProcessingStatus.Ready,
                ArchiveItem = db.ArchiveItems.Single(i => i.Title == title),
            };
            var files = new[] { Photo("Mill Street flood"), Photo("Market day"), Photo("Draft from 1900"), Photo("Ferry") };
            db.MediaFiles.AddRange(files);
            await db.SaveChangesAsync();
            return files.Select(f => f.Id).ToArray();
        });
        var (flood, market, draft, ferry) = (photos[0], photos[1], photos[2], photos[3]);
        async Task<string> CoverOfStreets()
        {
            var cards = await visitor.GetStringAsync("/collections");
            var card = cards[cards.IndexOf($"/collections/{streets}\"", StringComparison.Ordinal)..];
            return Regex.Match(card, @"/media/(\d+)/thumbnail").Groups[1].Value;
        }

        // Without a choice, the first photo of the collection's items.
        Assert.Equal(flood.ToString(), await CoverOfStreets());

        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(curator, streets, "Streets", "RecentlyAdded", market)).StatusCode);
        Assert.Equal(market.ToString(), await CoverOfStreets());

        // A draft's photo can be chosen ahead of publishing, but visitors do not see it until then.
        var edit = await curator.GetStringAsync($"/Admin/Collections/Edit/{streets}");
        Assert.Contains($"value=\"{draft}\"", edit);
        Assert.Contains("Not published", edit);
        Assert.DoesNotContain($"value=\"{ferry}\"", edit);
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(curator, streets, "Streets", "RecentlyAdded", draft)).StatusCode);
        Assert.Equal(flood.ToString(), await CoverOfStreets());
        Assert.DoesNotContain($"/media/{draft}/", await visitor.GetStringAsync("/collections"));

        // A photo from another collection is refused, and nothing is saved.
        var refused = await SaveAsync(curator, streets, "Streets renamed", "RecentlyAdded", ferry);
        Assert.Contains("Please choose one of the photos shown.", await refused.Content.ReadAsStringAsync());
        Assert.Equal("Streets", await archive.WithDbAsync(db => db.Collections.Where(c => c.Id == streets).Select(c => c.Title).SingleAsync()));

        // The chosen photo goes to the bin while its item stays published: the cover goes back to the first photo.
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(curator, streets, "Streets", "RecentlyAdded", market)).StatusCode);
        await archive.WithDbAsync(db => db.MediaFiles.Where(f => f.Id == market).ExecuteUpdateAsync(u => u.SetProperty(f => f.DeletedAt, DateTime.UtcNow)));
        Assert.Equal(flood.ToString(), await CoverOfStreets());
        await archive.WithDbAsync(db => db.MediaFiles.IgnoreQueryFilters().Where(f => f.Id == market).ExecuteUpdateAsync(u => u.SetProperty(f => f.DeletedAt, (DateTime?)null)));

        // The chosen photo's item moves to another collection: the cover goes back to the first photo.
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(curator, streets, "Streets", "RecentlyAdded", market)).StatusCode);
        await archive.WithDbAsync(db => db.ArchiveItems.Where(i => i.Title == "Market day").ExecuteUpdateAsync(u => u.SetProperty(i => i.CollectionId, harbour)));
        Assert.Equal(flood.ToString(), await CoverOfStreets());
    }

    [Fact]
    public async Task Only_an_empty_collection_can_be_deleted()
    {
        await using var archive = await ArchiveAsync(app);
        using var curator = await CuratorAsync(archive);
        var (binOnly, linkOnly, empty) = await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var withBin = new Collection { Title = "Only the bin", CreatedAt = now };
            var withLink = new Collection { Title = "Only a link", CreatedAt = now };
            var nothing = new Collection { Title = "Nothing in it", CreatedAt = now };
            var binned = new ArchiveItem { Title = "Binned", Collection = withBin, CreatedAt = now, UpdatedAt = now, Status = ItemStatus.Draft };
            binned.MoveToBin(now);
            db.AddRange(binned, nothing,
                new ContributionLink { Title = "Send it in", Collection = withLink, Token = ContributionLink.NewToken(), CreatedAt = now });
            await db.SaveChangesAsync();
            return (withBin.Id, withLink.Id, nothing.Id);
        });

        foreach (var id in new[] { binOnly, linkOnly, await CollectionIdAsync(archive, "Harbour") })
        {
            var page = $"/Admin/Collections/Edit/{id}";
            Assert.DoesNotContain("Delete the collection", await curator.GetStringAsync(page));
            var refused = await Browser.SendFormAsync(curator, $"{page}?handler=Delete", new(), page);
            Assert.Contains("Only an empty collection can be deleted.", await refused.Content.ReadAsStringAsync());
        }
        Assert.Contains("Items in the bin: 1", await curator.GetStringAsync($"/Admin/Collections/Edit/{binOnly}"));
        Assert.Contains("Contribution links: 1", await curator.GetStringAsync($"/Admin/Collections/Edit/{linkOnly}"));

        var emptyPage = $"/Admin/Collections/Edit/{empty}";
        Assert.Contains("Delete the collection", await curator.GetStringAsync(emptyPage));
        var deleted = await Browser.PostFormAsync(curator, $"{emptyPage}?handler=Delete", new(), emptyPage);
        Assert.Contains("The collection “Nothing in it” is deleted.", await deleted.Content.ReadAsStringAsync());

        Assert.Equal(3, await archive.WithDbAsync(db => db.Collections.CountAsync(c => c.Id == binOnly || c.Id == linkOnly || c.Title == "Harbour")));
        Assert.False(await archive.WithDbAsync(db => db.Collections.AnyAsync(c => c.Id == empty)));
        Assert.Contains("Deleted the empty collection", await curator.GetStringAsync("/Admin/Activity?what=Collections"));
    }

    [Fact]
    public async Task The_export_keeps_the_order_and_names_a_cover_only_when_its_file_is_in_it()
    {
        await using var archive = await ArchiveAsync(app);
        using var curator = await CuratorAsync(archive);
        var streets = await CollectionIdAsync(archive, "Streets");
        var draftPhoto = await archive.WithDbAsync(async db =>
        {
            var file = new MediaFile
            {
                OriginalFileName = "photo.jpg",
                ContentType = "image/jpeg",
                Sha256 = new string('0', 64),
                StorageId = Guid.NewGuid(),
                UploadedAt = DateTime.UtcNow,
                Status = ProcessingStatus.Ready,
                ArchiveItem = db.ArchiveItems.Single(i => i.Title == "Draft from 1900"),
            };
            db.MediaFiles.Add(file);
            await db.SaveChangesAsync();
            return file.Id;
        });
        await Browser.PostFormAsync(curator, $"/Admin/Collections?handler=MoveEarlier&id={streets}", new(), "/Admin/Collections");
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(curator, streets, "Streets", "OldestFirst", draftPhoto)).StatusCode);

        async Task<JsonElement> StreetsIn(bool publishedOnly)
        {
            var response = await Browser.PostFormAsync(curator, "/Admin/Export", new() { ["PublishedOnly"] = publishedOnly ? "true" : "false" });
            using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
            using var stream = zip.GetEntry("archive.json")!.Open();
            return JsonDocument.Parse(stream).RootElement.GetProperty("collections").EnumerateArray()
                .Single(c => c.GetProperty("title").GetString() == "Streets").Clone();
        }

        var everything = await StreetsIn(publishedOnly: false);
        Assert.Equal("OldestFirst", everything.GetProperty("sort").GetString());
        Assert.Equal(1, everything.GetProperty("position").GetInt32());
        Assert.Equal(draftPhoto, everything.GetProperty("coverFileId").GetInt32());
        // The draft and its photo are not in a copy of what is public, so neither is the choice of that photo.
        Assert.Equal(JsonValueKind.Null, (await StreetsIn(publishedOnly: true)).GetProperty("coverFileId").ValueKind);
    }
}
