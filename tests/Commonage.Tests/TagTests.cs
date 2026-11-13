using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

/// <summary>Tidying tags up: kinds with pages for visitors, renaming, merging and deleting. An archive of its own, so the tags are known.</summary>
[Collection(nameof(AppCollection))]
public class TagTests(CommonageAppFactory app)
{
    private static async Task<WebApplicationFactory<Program>> ArchiveAsync(CommonageAppFactory app)
    {
        var archive = await app.NewArchiveAsync();
        await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = "Town", CreatedAt = now };
            var tags = new Dictionary<string, Tag>();
            Tag TagFor(string name, TagKind kind) =>
                tags.TryGetValue(name, out var tag) ? tag : tags[name] = new Tag { Name = name, NormalizedName = Tag.Normalize(name), Kind = kind };
            ArchiveItem Item(string title, ItemStatus status, params Tag[] itemTags)
            {
                var item = new ArchiveItem { Title = title, Collection = collection, CreatedAt = now, UpdatedAt = now, Status = ItemStatus.Draft };
                item.SetTags(itemTags);
                if (status == ItemStatus.Published)
                    item.Publish(now);
                return item;
            }
            var millStreet = TagFor("Mill Street", TagKind.Place);
            var misspelt = TagFor("Mill St.", TagKind.Topic);
            var binned = Item("Old harbour", ItemStatus.Published, TagFor("harbour", TagKind.Place));
            binned.MoveToBin(now);
            db.ArchiveItems.AddRange(
                Item("Mill Street flood", ItemStatus.Published, millStreet, TagFor("flood", TagKind.Topic)),
                Item("Krause bakery", ItemStatus.Published, misspelt, TagFor("shops", TagKind.Topic)),
                Item("School on the corner", ItemStatus.Published, millStreet, misspelt),
                Item("Team photo", ItemStatus.Published, TagFor("Karl Becker", TagKind.Person)),
                Item("Letter, not yet checked", ItemStatus.Draft, TagFor("Anna Krause", TagKind.Person), TagFor("Öhringen", TagKind.Place)),
                binned);
            db.Tags.Add(new Tag { Name = "nothing here", NormalizedName = "nothing here" });
            return await db.SaveChangesAsync();
        });
        return archive;
    }

    private static Task<int> IdOfAsync(WebApplicationFactory<Program> archive, string name) =>
        archive.WithDbAsync(db => db.Tags.Where(t => t.Name == name).Select(t => t.Id).SingleAsync());

    /// <summary>The log's text without the markup and line breaks between an action and what it was about.</summary>
    private static string Activity(string page) =>
        System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(page, "<[^>]+>", ""), @"\s+", " ");

    private static async Task<HttpClient> CuratorAsync(WebApplicationFactory<Program> archive)
    {
        var curator = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        return curator;
    }

    [Fact]
    public async Task Visitors_find_every_place_and_person_of_published_items()
    {
        await using var archive = await ArchiveAsync(app);
        using var visitor = archive.CreateClient();

        var places = await visitor.GetStringAsync("/places");
        var people = await visitor.GetStringAsync("/people");

        Assert.Contains("href=\"/search?tag=Mill%20Street\">Mill Street</a>", places);
        Assert.Contains("aria-label=\"2 items\">(2)</span>", places);
        Assert.Contains("href=\"/search?tag=Karl%20Becker\">Karl Becker</a>", people);
        // Names only drafts or items in the bin have stay unseen.
        Assert.DoesNotContain("Anna Krause", people);
        Assert.DoesNotContain("Öhringen", places);
        Assert.DoesNotContain("harbour", places);
        // Topics are not listed as places.
        Assert.DoesNotContain("flood", places);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/topics")).StatusCode);
    }

    [Fact]
    public async Task Places_and_people_lead_to_their_pages_from_the_start_page_and_the_item()
    {
        await using var archive = await ArchiveAsync(app);
        using var visitor = archive.CreateClient();
        var floodId = await archive.WithDbAsync(db => db.ArchiveItems.Where(i => i.Title == "Mill Street flood").Select(i => i.Id).SingleAsync());

        var home = await visitor.GetStringAsync("/");
        var item = await visitor.GetStringAsync($"/items/{floodId}");

        Assert.Contains("href=\"/places\">All places</a>", home);
        Assert.Contains("href=\"/people\">All people</a>", home);
        Assert.Contains(">Places</a>", item);
        Assert.Contains(">Topics</span>", item);
    }

    [Fact]
    public async Task Names_are_listed_under_their_first_letter()
    {
        Assert.Equal("O", Commonage.Web.Pages.TagIndexModel.InitialOf("Öhringen"));
        Assert.Equal("M", Commonage.Web.Pages.TagIndexModel.InitialOf("mill street"));
        Assert.Equal("#", Commonage.Web.Pages.TagIndexModel.InitialOf("12 Mill Street"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_new_name_reaches_every_item_and_the_search()
    {
        await using var archive = await ArchiveAsync(app);
        using var curator = await CuratorAsync(archive);
        var id = await IdOfAsync(archive, "harbour");
        var page = $"/Admin/Tags/Edit/{id}";

        await Browser.PostFormAsync(curator, page, new() { ["Input.Name"] = "Harbour front", ["Input.Kind"] = "Place" });

        // The item in the bin has the new name too, for when it is restored.
        var tagNames = await archive.WithDbAsync(db => db.ArchiveItems.IgnoreQueryFilters().Where(i => i.Title == "Old harbour").Select(i => i.TagNames).SingleAsync());
        Assert.Equal("Harbour front", tagNames);

        var flood = await IdOfAsync(archive, "flood");
        await Browser.PostFormAsync(curator, $"/Admin/Tags/Edit/{flood}", new() { ["Input.Name"] = "Floods", ["Input.Kind"] = "Topic" });
        Assert.Contains("Mill Street flood", await curator.GetStringAsync("/search?q=floods"));
        Assert.Contains("Changed the tag: flood → Floods", Activity(await curator.GetStringAsync("/Admin/Activity")));
    }

    [Fact]
    public async Task A_name_another_tag_has_is_refused_and_merging_is_offered()
    {
        await using var archive = await ArchiveAsync(app);
        using var curator = await CuratorAsync(archive);
        var id = await IdOfAsync(archive, "Mill St.");
        var page = $"/Admin/Tags/Edit/{id}";

        var taken = await Browser.SendFormAsync(curator, page, new() { ["Input.Name"] = "mill street", ["Input.Kind"] = "Topic" });
        var comma = await Browser.SendFormAsync(curator, page, new() { ["Input.Name"] = "Mill, Street", ["Input.Kind"] = "Topic" });
        await Browser.PostFormAsync(curator, page, new() { ["Input.Name"] = "MILL ST.", ["Input.Kind"] = "Topic" });

        var refused = await taken.Content.ReadAsStringAsync();
        Assert.Contains("There is already a tag “Mill Street”. To make one tag of the two, merge them below.", refused);
        Assert.Contains("value=\"Mill Street\"", refused);
        var commaPage = await comma.Content.ReadAsStringAsync();
        Assert.Contains("A tag name cannot contain commas or semicolons.", commaPage);
        // The typed name is kept and leaving asks first, but not when merging is what the page suggests.
        Assert.Contains("data-warn-unsaved=\"dirty\"", commaPage);
        Assert.Contains("data-warn-unsaved=\"\"", refused);
        // Its own name in other letters is no other tag.
        Assert.Equal("MILL ST.", await archive.WithDbAsync(db => db.Tags.Where(t => t.Id == id).Select(t => t.Name).SingleAsync()));
    }

    [Fact]
    public async Task Merging_moves_every_item_to_the_other_tag_once()
    {
        await using var archive = await ArchiveAsync(app);
        using var curator = await CuratorAsync(archive);
        var id = await IdOfAsync(archive, "Mill St.");
        var page = $"/Admin/Tags/Edit/{id}";

        var merged = await Browser.PostFormAsync(curator, $"{page}?handler=Merge", new() { ["MergeInto"] = "mill street" }, page);

        Assert.Contains("“Mill St.” is merged into “Mill Street”, on 2 items.", await merged.Content.ReadAsStringAsync());
        var items = await archive.WithDbAsync(db => db.ArchiveItems.Where(i => i.Title == "Krause bakery" || i.Title == "School on the corner")
            .OrderBy(i => i.Title).Select(i => i.TagNames).ToListAsync());
        Assert.Equal(["shops Mill Street", "Mill Street"], items);
        Assert.False(await archive.WithDbAsync(db => db.Tags.AnyAsync(t => t.Name == "Mill St.")));
        Assert.Contains("Items found: 3", await curator.GetStringAsync("/search?tag=Mill+Street"));
        Assert.Contains("Merged two tags into one: Mill St. → Mill Street", Activity(await curator.GetStringAsync("/Admin/Activity")));
    }

    [Fact]
    public async Task Merging_into_a_tag_that_does_not_exist_or_itself_is_refused()
    {
        await using var archive = await ArchiveAsync(app);
        using var curator = await CuratorAsync(archive);
        var id = await IdOfAsync(archive, "Mill St.");
        var page = $"/Admin/Tags/Edit/{id}";

        var unknown = await Browser.SendFormAsync(curator, $"{page}?handler=Merge", new() { ["MergeInto"] = "Mill Road" }, page);
        var itself = await Browser.SendFormAsync(curator, $"{page}?handler=Merge", new() { ["MergeInto"] = "mill st." }, page);

        Assert.Contains("There is no tag “Mill Road”.", await unknown.Content.ReadAsStringAsync());
        Assert.Contains("Please choose a different tag to merge this one into.", await itself.Content.ReadAsStringAsync());
        Assert.Equal(2, await archive.WithDbAsync(db => db.ArchiveItems.CountAsync(i => i.Tags.Any(t => t.Id == id))));
    }

    [Fact]
    public async Task Only_tags_no_item_has_can_be_deleted_not_even_one_in_the_bin()
    {
        await using var archive = await ArchiveAsync(app);
        using var curator = await CuratorAsync(archive);
        var harbour = await IdOfAsync(archive, "harbour");
        var page = $"/Admin/Tags/Edit/{harbour}";

        var editPage = await curator.GetStringAsync(page);
        var refused = await Browser.SendFormAsync(curator, $"{page}?handler=Delete", new(), page);
        var list = await curator.GetStringAsync("/Admin/Tags");
        await Browser.PostFormAsync(curator, "/Admin/Tags?handler=DeleteUnused", new(), "/Admin/Tags");

        Assert.Contains("One more item in the bin has it.", editPage);
        Assert.DoesNotContain("Delete the tag", editPage);
        Assert.Contains("Only a tag that no item has can be deleted.", await refused.Content.ReadAsStringAsync());
        Assert.Contains("One tag is not on any item, not even one in the bin.", list);
        var left = await archive.WithDbAsync(db => db.Tags.Select(t => t.Name).ToListAsync());
        Assert.Contains("harbour", left);
        Assert.DoesNotContain("nothing here", left);
        Assert.Contains("Deleted all unused tags: nothing here", Activity(await curator.GetStringAsync("/Admin/Activity")));
    }

    [Fact]
    public async Task The_tag_list_can_be_narrowed_down()
    {
        await using var archive = await ArchiveAsync(app);
        using var curator = await CuratorAsync(archive);
        string[] all = ["Anna Krause", "flood", "harbour", "Karl Becker", "Mill St.", "Mill Street", "nothing here", "Öhringen", "shops"];
        async Task<IReadOnlyList<string>> ShownAsync(string query)
        {
            var page = await curator.GetStringAsync("/Admin/Tags" + query);
            return all.Where(name => page.Contains($">{System.Net.WebUtility.HtmlEncode(name)}</a>")).ToList();
        }

        Assert.Equal(["Anna Krause", "Karl Becker"], await ShownAsync("?kind=Person"));
        Assert.Equal(["Mill St.", "Mill Street"], await ShownAsync("?q=mill"));
        Assert.Equal(["nothing here"], await ShownAsync("?unused=true"));
        Assert.Contains("Items: 2", await curator.GetStringAsync("/Admin/Tags?q=Mill+Street"));
        // As many as the item list finds: the one item in the bin is not among them.
        Assert.Contains("Items: 0", await curator.GetStringAsync("/Admin/Tags?q=harbour"));
    }

    [Fact]
    public async Task Visitors_cannot_change_tags()
    {
        await using var archive = await ArchiveAsync(app);
        using var visitor = archive.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var id = await IdOfAsync(archive, "flood");

        Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync("/Admin/Tags")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync($"/Admin/Tags/Edit/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_copy_of_what_is_public_names_no_one_from_drafts()
    {
        await using var archive = await ArchiveAsync(app);
        using var curator = await CuratorAsync(archive);

        var response = await Browser.PostFormAsync(curator, "/Admin/Export", new() { ["PublishedOnly"] = "true" });
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        using var stream = zip.GetEntry("archive.json")!.Open();
        var tags = JsonDocument.Parse(stream).RootElement.GetProperty("tags").EnumerateArray()
            .Select(t => $"{t.GetProperty("name").GetString()}: {t.GetProperty("kind").GetString()}").ToList();

        Assert.Equal(["Karl Becker: Person", "Mill St.: Topic", "Mill Street: Place", "flood: Topic", "shops: Topic"], tags);
    }
}
