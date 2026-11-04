using System.Text.RegularExpressions;
using Commonage.Web.Domain;
using Commonage.Web.Search;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class CuratorListTests(CommonageAppFactory app)
{
    private Task<Collection> CreateCollectionWithDraftsAsync(params (string Title, string? Date)[] items) => app.WithDbAsync(async db =>
    {
        var now = DateTime.UtcNow;
        var collection = new Collection { Title = TestData.Unique("Shoebox"), CreatedAt = now };
        db.Collections.Add(collection);
        for (var i = 0; i < items.Length; i++)
        {
            var item = new ArchiveItem
            {
                Title = items[i].Title,
                Collection = collection,
                Status = ItemStatus.Draft,
                CreatedAt = now.AddMinutes(i),
                UpdatedAt = now.AddMinutes(i),
            };
            item.TrySetDate(items[i].Date);
            db.ArchiveItems.Add(item);
        }
        await db.SaveChangesAsync();
        return collection;
    });

    private Task<int> IdOfAsync(int collectionId, string title) =>
        app.WithDbAsync(db => db.ArchiveItems.Where(i => i.CollectionId == collectionId && i.Title == title).Select(i => i.Id).SingleAsync());

    private async Task<HttpClient> CuratorAsync()
    {
        var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        return curator;
    }

    [Fact]
    public async Task The_list_filters_by_collection_and_by_what_is_still_missing()
    {
        var collection = await CreateCollectionWithDraftsAsync(("Has a date", "1950"), ("Has no date", null));
        using var curator = await CuratorAsync();

        var page = await curator.GetStringAsync($"/Admin?collection={collection.Id}&needs=Date");

        Assert.Contains("Has no date", page);
        Assert.DoesNotContain("Has a date", page);
    }

    [Fact]
    public async Task The_list_can_be_sorted_by_title()
    {
        var collection = await CreateCollectionWithDraftsAsync(("Beta", null), ("Alpha", null));
        using var curator = await CuratorAsync();

        var page = await curator.GetStringAsync($"/Admin?collection={collection.Id}&sort=Title");

        Assert.True(page.IndexOf("Alpha", StringComparison.Ordinal) < page.IndexOf("Beta", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Waiting_contributions_are_listed_oldest_first()
    {
        var older = await app.CreateContributionAsync(ItemStatus.Submitted);
        var newer = await app.CreateContributionAsync(ItemStatus.Submitted);
        using var curator = await CuratorAsync();

        var page = await curator.GetStringAsync("/Admin?status=Submitted");

        Assert.True(page.IndexOf(older.Title, StringComparison.Ordinal) < page.IndexOf(newer.Title, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Save_and_next_saves_and_goes_on_to_the_next_item_of_the_same_list()
    {
        var collection = await CreateCollectionWithDraftsAsync(("First", null), ("Second", null), ("Third", null));
        var (first, second) = (await IdOfAsync(collection.Id, "First"), await IdOfAsync(collection.Id, "Second"));
        var list = $"/Admin?collection={collection.Id}&sort=OldestFirst";
        using var curator = await CuratorAsync();
        var page = $"/Admin/Items/Edit/{first}?back={Uri.EscapeDataString(list)}";

        var opened = await curator.GetStringAsync(page);
        var response = await Browser.PostFormAsync(curator, page, new()
        {
            ["Input.Title"] = "First",
            ["Input.CollectionId"] = collection.Id.ToString(),
            ["Input.Description"] = "Described",
            ["then"] = "next",
        });

        Assert.Contains("Save and next", opened);
        Assert.Contains($"/Admin/Items/Edit/{second}", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal(list, Uri.UnescapeDataString(Regex.Match(response.RequestMessage.RequestUri.Query, "back=([^&]+)").Groups[1].Value));
        Assert.Equal("Described", await app.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == first).Select(i => i.Description).SingleAsync()));
    }

    [Fact]
    public async Task The_last_item_of_a_list_offers_no_next_one_and_an_item_opened_elsewhere_neither()
    {
        var collection = await CreateCollectionWithDraftsAsync(("Only", null));
        var only = await IdOfAsync(collection.Id, "Only");
        using var curator = await CuratorAsync();

        var inList = await curator.GetStringAsync($"/Admin/Items/Edit/{only}?back={Uri.EscapeDataString($"/Admin?collection={collection.Id}")}");
        var alone = await curator.GetStringAsync($"/Admin/Items/Edit/{only}");

        Assert.DoesNotContain("Save and next", inList);
        Assert.DoesNotContain("Save and next", alone);
    }

    [Fact]
    public async Task A_way_back_can_only_lead_to_the_list()
    {
        var collection = await CreateCollectionWithDraftsAsync(("Only", null));
        var only = await IdOfAsync(collection.Id, "Only");
        using var curator = await CuratorAsync();

        var page = await curator.GetStringAsync($"/Admin/Items/Edit/{only}?back={Uri.EscapeDataString("https://example.org/Admin")}");

        Assert.DoesNotContain("example.org/Admin", page);
    }

    [Fact]
    public async Task Save_and_add_another_opens_a_new_item_in_the_same_collection()
    {
        var collection = await CreateCollectionWithDraftsAsync();
        using var curator = await CuratorAsync();

        var response = await Browser.PostFormAsync(curator, "/Admin/Items/Edit", new()
        {
            ["Input.Title"] = "One of many",
            ["Input.CollectionId"] = collection.Id.ToString(),
            ["then"] = "another",
        });

        Assert.Equal("/Admin/Items/Edit", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains($"collectionId={collection.Id}", response.RequestMessage.RequestUri.Query);
        Assert.Equal(1, await app.WithDbAsync(db => db.ArchiveItems.CountAsync(i => i.CollectionId == collection.Id)));
    }

    [Fact]
    public async Task After_a_decision_on_a_contribution_the_next_waiting_one_opens()
    {
        var decided = await app.CreateContributionAsync(ItemStatus.Submitted);
        using var curator = await CuratorAsync();

        var response = await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{decided.Id}", new()
        {
            ["Input.Title"] = decided.Title,
            ["Input.CollectionId"] = decided.CollectionId.ToString(),
            ["command"] = "accept",
        });

        var nextWaiting = await app.WithDbAsync(db => db.ArchiveItems
            .Where(i => i.Status == ItemStatus.Submitted)
            .OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
            .Select(i => (int?)i.Id)
            .FirstOrDefaultAsync());
        var expected = nextWaiting is { } id ? $"/Admin/Items/Edit/{id}" : "/Admin";
        Assert.Equal(expected, response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Contains("accepted as a draft", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task On_a_review_the_photos_come_before_the_decision()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Submitted);
        using var curator = await CuratorAsync();

        var page = await curator.GetStringAsync($"/Admin/Items/Edit/{item.Id}");

        Assert.True(page.IndexOf("photos-heading", StringComparison.Ordinal) < page.IndexOf("Save and publish", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_curator_can_preview_a_draft_and_a_visitor_still_cannot_see_it()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Submitted);
        using var curator = await CuratorAsync();
        await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{item.Id}", "Upload", "flood.jpg", TestData.Photo);
        using var visitor = app.CreateClient();

        var preview = await curator.GetStringAsync($"/Admin/Items/Preview/{item.Id}");

        Assert.Contains(item.Title, preview);
        Assert.Contains("Visitors cannot see this item", preview);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await visitor.GetAsync($"/items/{item.Id}")).StatusCode);
        Assert.Contains("/Account/Login", (await visitor.GetAsync($"/Admin/Items/Preview/{item.Id}")).RequestMessage!.RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("circa 1952", true, "1947–1957")]
    [InlineData("1953", true, "1953")]
    [InlineData("the winter when I was small", false, "could not be understood")]
    public async Task The_date_check_says_how_a_date_is_understood(string text, bool understood, string expected)
    {
        using var visitor = app.CreateClient();

        var answer = await visitor.GetStringAsync($"/date-check?text={Uri.EscapeDataString(text)}");

        Assert.Contains($"\"understood\":{understood.ToString().ToLowerInvariant()}", answer);
        Assert.Contains(expected, answer);
    }

    [Fact]
    public async Task A_form_sent_with_a_mistake_lists_it_with_a_link_to_the_field()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Draft);
        using var curator = await CuratorAsync();

        var response = await Browser.SendFormAsync(curator, $"/Admin/Items/Edit/{item.Id}", new()
        {
            ["Input.Title"] = item.Title,
            ["Input.CollectionId"] = item.CollectionId.ToString(),
            ["Input.Date"] = "not a date",
        });
        var page = await response.Content.ReadAsStringAsync();

        Assert.Contains("href=\"#Input_Date\"", page);
        Assert.Contains("data-focus-on-load", page);
        Assert.Matches(@"id=""Input_Date""[^>]*aria-invalid=""true""|aria-invalid=""true""[^>]*id=""Input_Date""", page);
        Assert.Matches(@"aria-describedby=""date-help date-result Input_Date-error""", page);
        Assert.Contains("id=\"Input_Date-error\"", page);
        Assert.Contains("data-warn-unsaved=\"dirty\"", page);
    }

    [Fact]
    public void A_list_address_gives_back_its_filter()
    {
        var filter = AdminItemList.FilterFrom("/Admin?status=Draft&collection=7&needs=Date&sort=Title&q=flood&tag=mill");

        Assert.Equal(new AdminItemFilter("flood", ItemStatus.Draft, 7, "mill", ItemNeed.Date, AdminItemSort.Title), filter);
        Assert.Equal(new AdminItemFilter(), AdminItemList.FilterFrom("/Admin"));
        Assert.Null(AdminItemList.FilterFrom("/Admin/Users"));
        Assert.Null(AdminItemList.FilterFrom("https://example.org/Admin"));
        Assert.Null(AdminItemList.FilterFrom(null));
    }
}
