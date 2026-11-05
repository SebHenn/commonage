using System.Text.RegularExpressions;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public partial class SearchTests(CommonageAppFactory app)
{
    [Fact]
    public async Task Search_matches_word_beginnings_in_titles_descriptions_and_tags_but_never_drafts()
    {
        var word = UniqueWord();
        var collection = await SeedAsync(
            Item($"The {word}mill", description: null),
            Item("Old schools", description: $"All {word}schulen of the town"),
            Item("Harbour", tags: [$"{word}tag"]),
            Item($"Unfinished {word}", status: ItemStatus.Draft));
        using var visitor = app.CreateClient();

        var results = await visitor.GetStringAsync($"/search?q={word}");
        var noResults = await visitor.GetStringAsync($"/search?q={word}+{UniqueWord()}");

        Assert.Contains($"The {word}mill", results);
        Assert.Contains("Old schools", results);
        Assert.Contains("Harbour", results);
        Assert.DoesNotContain("Unfinished", results);
        Assert.Equal(0, CountCards(noResults));
    }

    [Fact]
    public async Task Search_ignores_accents_in_both_directions()
    {
        var word = UniqueWord();
        await SeedAsync(
            Item($"Die Mühle {word}"),
            Item($"Café am Markt {word}"),
            Item($"Mühlenstraße {word}"));
        using var visitor = app.CreateClient();

        var mill = await visitor.GetStringAsync($"/search?q=muhle+{word}");
        var cafe = await visitor.GetStringAsync($"/search?q=cafe+{word}");
        var street = await visitor.GetStringAsync($"/search?q={Uri.EscapeDataString("MÜHLENSTRASSE")}+{word}");

        Assert.Equal([$"Die Mühle {word}", $"Mühlenstraße {word}"], CardTitles(mill).Order());
        Assert.Equal([$"Café am Markt {word}"], CardTitles(cafe));
        Assert.Equal([$"Mühlenstraße {word}"], CardTitles(street));
    }

    [Fact]
    public async Task Filters_by_period_tag_and_collection()
    {
        var tag = $"Harbour {UniqueWord()}";
        var collectionId = await SeedAsync(
            Item("Ferry around 1950", date: "circa 1950", tags: [tag]),
            Item("Ferry in 1962", date: "1962"),
            Item("Ferry, undated"));
        using var visitor = app.CreateClient();

        var sixties = await visitor.GetStringAsync($"/search?collection={collectionId}&from=1956&to=1970");
        var year1950 = await visitor.GetStringAsync($"/search?collection={collectionId}&from=1950&to=1950");
        var byTag = await visitor.GetStringAsync($"/search?collection={collectionId}&tag={Uri.EscapeDataString(tag.ToUpperInvariant())}");

        Assert.Equal(["Ferry in 1962"], CardTitles(sixties));
        Assert.Equal(["Ferry around 1950"], CardTitles(year1950));
        Assert.Equal(["Ferry around 1950"], CardTitles(byTag));
    }

    [Fact]
    public async Task Sorting_by_date_puts_undated_items_last()
    {
        var collectionId = await SeedAsync(
            Item("Undated"),
            Item("Sixties", date: "1960s"),
            Item("Before the war", date: "vor 1939"),
            Item("Fifties", date: "1955"));
        using var visitor = app.CreateClient();

        var oldestFirst = await visitor.GetStringAsync($"/search?collection={collectionId}&sort=OldestFirst");
        var newestFirst = await visitor.GetStringAsync($"/search?collection={collectionId}&sort=NewestFirst");

        Assert.Equal(["Before the war", "Fifties", "Sixties", "Undated"], CardTitles(oldestFirst));
        Assert.Equal(["Sixties", "Fifties", "Before the war", "Undated"], CardTitles(newestFirst));
    }

    [Fact]
    public async Task Long_result_lists_are_split_into_pages_that_keep_the_filters()
    {
        var collectionId = await SeedAsync(Enumerable.Range(1, 30).Select(n => Item($"Photo {n}")).ToArray());
        using var visitor = app.CreateClient();

        var firstPage = await visitor.GetStringAsync($"/search?collection={collectionId}");
        var secondPage = await visitor.GetStringAsync($"/search?collection={collectionId}&p=2");
        var collectionSecondPage = await visitor.GetStringAsync($"/collections/{collectionId}?p=2");

        Assert.Equal(24, CountCards(firstPage));
        Assert.Contains($"href=\"/search?collection={collectionId}&amp;p=2\"", firstPage);
        Assert.Equal(6, CountCards(secondPage));
        Assert.Contains("Page 2 of 2", secondPage);
        Assert.Equal(6, CountCards(collectionSecondPage));
    }

    [Fact]
    public async Task A_curator_adds_a_date_and_tags_that_visitors_can_filter_by()
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var collectionId = await SeedAsync();
        var word = UniqueWord();
        var tag = $"Mill {word}";

        var rejected = await Browser.PostFormAsync(curator, "/Admin/Items/Edit", new()
        {
            ["Input.Title"] = "Shop front",
            ["Input.CollectionId"] = collectionId.ToString(),
            ["Input.Date"] = "someday",
        });
        await Browser.PostFormAsync(curator, "/Admin/Items/Edit", new()
        {
            ["Input.Title"] = "Shop front",
            ["Input.CollectionId"] = collectionId.ToString(),
            ["Input.Date"] = "um 1950",
            ["Input.Tags"] = $"{tag}, {tag.ToUpperInvariant()}, Shops {word}",
            ["command"] = "publish",
        });

        Assert.Contains("This date could not be understood.", await rejected.Content.ReadAsStringAsync());
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
        var item = await db.ArchiveItems.Include(i => i.Tags).SingleAsync(i => i.CollectionId == collectionId);
        Assert.Equal((new DateOnly(1945, 1, 1), new DateOnly(1955, 12, 31)), (item.DateFrom!.Value, item.DateTo!.Value));
        Assert.Equal([tag, $"Shops {word}"], item.Tags.Select(t => t.Name).Order());

        using var visitor = app.CreateClient();
        var itemPage = await visitor.GetStringAsync($"/items/{item.Id}");
        var byTag = await visitor.GetStringAsync($"/search?tag={Uri.EscapeDataString(tag)}");
        Assert.Contains("um 1950", itemPage);
        Assert.Contains($"href=\"/search?tag={Uri.EscapeDataString(tag)}\"", itemPage);
        Assert.Equal(["Shop front"], CardTitles(byTag));
    }

    private record SeedItem(string Title, string? Description, string? Date, string[] Tags, ItemStatus Status);

    private static SeedItem Item(
        string title, string? description = null, string? date = null, string[]? tags = null, ItemStatus status = ItemStatus.Published) =>
        new(title, description, date, tags ?? [], status);

    /// <summary>Creates a new collection with the given items, published one second apart in the given order.</summary>
    private async Task<int> SeedAsync(params SeedItem[] items)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
        var now = DateTime.UtcNow;
        var collection = new Collection { Title = $"Search test {Guid.NewGuid():N}", CreatedAt = now };
        db.Add(collection);
        foreach (var (seed, index) in items.Select((seed, index) => (seed, index)))
        {
            var item = new ArchiveItem { Title = seed.Title, Description = seed.Description, Collection = collection, CreatedAt = now, UpdatedAt = now };
            Assert.True(item.TrySetDate(seed.Date));
            item.SetTags(seed.Tags.Select(name => new Tag { Name = name, NormalizedName = Tag.Normalize(name) }));
            if (seed.Status == ItemStatus.Published)
                item.Publish(now.AddSeconds(-index));
            db.Add(item);
        }

        await db.SaveChangesAsync();
        return collection.Id;
    }

    /// <summary>A word no other test uses, so tests sharing the database cannot see each other's items.</summary>
    private static string UniqueWord() => "x" + string.Concat(Guid.NewGuid().ToString("N").Select(c => char.IsDigit(c) ? (char)('g' + c - '0') : c));

    private static int CountCards(string html) => ItemCard().Matches(html).Count;

    private static string[] CardTitles(string html) =>
        ItemCard().Matches(html).Select(m => CardTitle().Match(m.Value).Groups[1].Value.Trim()).ToArray();

    [GeneratedRegex(@"<li[^>]*>\s*<a[^>]*item-card[^>]*>.*?</li>", RegexOptions.Singleline)]
    private static partial Regex ItemCard();

    // The title has its own element in the card.
    [GeneratedRegex(@"class=""item-card-title"">\s*([^<]+?)\s*</span>", RegexOptions.Singleline)]
    private static partial Regex CardTitle();
}
