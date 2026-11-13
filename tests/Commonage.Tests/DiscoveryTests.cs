using System.Net;
using System.Text.RegularExpressions;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Commonage.Tests;

/// <summary>Finding items by when they are from: dates typed into the search, and the decades. An archive of its own, so the counts are known.</summary>
[Collection(nameof(AppCollection))]
public partial class DiscoveryTests(CommonageAppFactory app)
{
    private static async Task<WebApplicationFactory<Program>> ArchiveAsync(CommonageAppFactory app)
    {
        var archive = await app.NewArchiveAsync();
        await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = "Town", CreatedAt = now };
            ArchiveItem Item(string title, string? date, ItemStatus status = ItemStatus.Published, string? description = null)
            {
                var item = new ArchiveItem { Title = title, Description = description, Collection = collection, CreatedAt = now, UpdatedAt = now, Status = ItemStatus.Draft };
                Assert.True(item.TrySetDate(date), $"{date} is a date");
                if (status == ItemStatus.Published)
                    item.Publish(now);
                return item;
            }
            db.ArchiveItems.AddRange(
                Item("Tram to the harbour", "1930-1955", description: "The line ran along the river."),
                Item("Mill Street flood", "1956"),
                Item("Building the bridge", "1955-1965"),
                Item("Mill Street school", "1932"),
                Item("Team photo 1958", null),
                Item("Market before the war", "before 1945"),
                Item("Unfinished draft", "1953", ItemStatus.Draft));
            return await db.SaveChangesAsync();
        });
        return archive;
    }

    private static readonly string[] Titles =
        ["Tram to the harbour", "Mill Street flood", "Building the bridge", "Mill Street school", "Team photo 1958", "Market before the war", "Unfinished draft"];

    private static IReadOnlyList<string> Found(string page) => Titles.Where(page.Contains).ToList();

    [GeneratedRegex(@"Items found: (\d+)")]
    private static partial Regex ItemsFound();

    [GeneratedRegex(@"href=""(/search\?from=(\d+)&amp;to=\d+)""[^>]*aria-label=""\d+s: (\d+) items?""")]
    private static partial Regex DecadeLink();

    [Fact]
    public async Task A_decade_typed_into_the_search_finds_what_is_dated_in_it()
    {
        await using var archive = await ArchiveAsync(app);
        using var visitor = archive.CreateClient();

        var decade = await visitor.GetStringAsync("/search?q=1950s");
        var withWords = await visitor.GetStringAsync("/search?q=Mill+Street+1950s");
        var year = await visitor.GetStringAsync("/search?q=1958");

        Assert.Equal(["Tram to the harbour", "Mill Street flood", "Building the bridge"], Found(decade));
        Assert.Contains("“1950s” also finds what is dated 1950–1959.", decade);
        Assert.Equal(["Mill Street flood"], Found(withWords));
        // The bridge by its date; the team photo is undated, but the year is in its title.
        Assert.Equal(["Building the bridge", "Team photo 1958"], Found(year));
    }

    [Fact]
    public async Task Curators_search_the_same_way_and_also_find_drafts()
    {
        await using var archive = await ArchiveAsync(app);
        using var curator = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);

        var page = await curator.GetStringAsync("/Admin?q=1950s");

        Assert.Equal(["Tram to the harbour", "Mill Street flood", "Building the bridge", "Unfinished draft"], Found(page));
    }

    [Fact]
    public async Task Each_decade_on_the_start_page_finds_as_many_items_as_it_says()
    {
        await using var archive = await ArchiveAsync(app);
        using var visitor = archive.CreateClient();

        var home = await visitor.GetStringAsync("/");
        var decades = DecadeLink().Matches(home).Select(m => (Url: WebUtility.HtmlDecode(m.Groups[1].Value), Decade: m.Groups[2].Value, Count: int.Parse(m.Groups[3].Value))).ToList();

        Assert.Contains("Through the decades", home);
        // The market "before 1945" reaches back to the first decade, as the year filter finds it. The draft is not counted.
        Assert.Equal([("1930", 3), ("1940", 2), ("1950", 3), ("1960", 1)], decades.Select(d => (d.Decade, d.Count)));
        foreach (var (url, decade, count) in decades)
        {
            var found = ItemsFound().Match(await visitor.GetStringAsync(url)).Groups[1].Value;
            Assert.True(found == count.ToString(), $"The {decade}s say {count} items, but tapping them finds {found}.");
        }
    }

    [Fact]
    public async Task The_decades_of_a_search_keep_its_words()
    {
        await using var archive = await ArchiveAsync(app);
        using var visitor = archive.CreateClient();

        var page = await visitor.GetStringAsync("/search?q=Mill");

        Assert.Contains("href=\"/search?q=Mill&amp;from=1930&amp;to=1939\"", page);
        Assert.Contains("href=\"/search?q=Mill&amp;from=1950&amp;to=1959\"", page);
        Assert.DoesNotContain("from=1940", page);
    }

    [Fact]
    public async Task An_archive_without_dates_shows_no_timeline()
    {
        await using var archive = await app.NewArchiveAsync();
        using var visitor = archive.CreateClient();

        Assert.DoesNotContain("Through the decades", await visitor.GetStringAsync("/"));
        Assert.DoesNotContain("decade-timeline", await visitor.GetStringAsync("/search"));
    }
}
