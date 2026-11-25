using System.Net;
using System.Text.RegularExpressions;
using Commonage.Web.Domain;
using Commonage.Web.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

/// <summary>Stories as curators write them: parts in order, refusals, versions and the activity log. Each test has an archive of its own.</summary>
[Collection(nameof(AppCollection))]
public class StoryEditTests(CommonageAppFactory app)
{
    private sealed record Seed(int Published, int Draft, int Binned, int Withdrawn);

    private static async Task<(WebApplicationFactory<Program> Archive, HttpClient Curator, Seed Items)> StartAsync(CommonageAppFactory app)
    {
        var archive = await app.NewArchiveAsync();
        var curator = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        var items = await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = "Streets", CreatedAt = now };
            ArchiveItem Item(string title, ItemStatus status)
            {
                var item = new ArchiveItem { Title = title, Collection = collection, CreatedAt = now, UpdatedAt = now, Status = ItemStatus.Draft };
                if (status == ItemStatus.Published)
                    item.Publish(now);
                return item;
            }

            var published = Item("Published flood", ItemStatus.Published);
            var draft = Item("Draft flood", ItemStatus.Draft);
            var binned = Item("Binned flood", ItemStatus.Published);
            binned.MoveToBin(now);
            var withdrawn = Item("Withdrawn flood", ItemStatus.Published);
            withdrawn.Status = ItemStatus.Rejected;
            db.ArchiveItems.AddRange(published, draft, binned, withdrawn);
            await db.SaveChangesAsync();
            return new Seed(published.Id, draft.Id, binned.Id, withdrawn.Id);
        });
        return (archive, curator, items);
    }

    private static async Task<int> CreateAsync(HttpClient curator, string title = "Mill Street")
    {
        var response = await Browser.PostFormAsync(curator, "/Admin/Stories?handler=Create", new() { ["NewTitle"] = title }, "/Admin/Stories");
        return int.Parse(Regex.Match(response.RequestMessage!.RequestUri!.AbsolutePath, @"/Admin/Stories/Edit/(\d+)").Groups[1].Value);
    }

    private static string Page(int id) => $"/Admin/Stories/Edit/{id}";

    private static Task<HttpResponseMessage> PostAsync(HttpClient curator, int id, string handler, Dictionary<string, string> fields, int? blockId = null) =>
        Browser.SendFormAsync(curator, $"{Page(id)}?handler={handler}" + (blockId is { } b ? $"&blockId={b}" : ""), fields, Page(id));

    private static Task<HttpResponseMessage> AddTextAsync(HttpClient curator, int id, string text) => PostAsync(curator, id, "AddText", new() { ["text"] = text });

    private static Task<HttpResponseMessage> AddItemAsync(HttpClient curator, int id, string reference, string caption = "") =>
        PostAsync(curator, id, "AddItem", new() { ["item"] = reference, ["caption"] = caption });

    private static Task<List<StoryBlock>> BlocksAsync(WebApplicationFactory<Program> archive, int id) =>
        archive.WithDbAsync(db => db.StoryBlocks.Where(b => b.StoryId == id).OrderBy(b => b.Position).ThenBy(b => b.Id).ToListAsync());

    private static Task<List<AuditAction>> ActionsAsync(WebApplicationFactory<Program> archive, int id) =>
        archive.WithDbAsync(db => db.AuditEntries.Where(e => e.StoryId == id).OrderBy(e => e.Id).Select(e => e.Action).ToListAsync());

    [Fact]
    public async Task A_curator_creates_edits_publishes_and_unpublishes_a_story()
    {
        var (archive, curator, _) = await StartAsync(app);
        await using (archive)
        using (curator)
        {
            var id = await CreateAsync(curator);
            var created = await archive.WithDbAsync(db => db.Stories.SingleAsync(s => s.Id == id));
            Assert.Equal(("Mill Street", StoryStatus.Draft), (created.Title, created.Status));

            var form = await curator.GetStringAsync(Page(id));
            var version = System.Net.WebUtility.HtmlDecode(Regex.Match(form, @"name=""Input.Version""[^>]*value=""(\d+)""").Groups[1].Value);
            var saved = await Browser.PostFormAsync(curator, Page(id), new() { ["Input.Title"] = "Mill Street, 1962", ["Input.Summary"] = "A flood.", ["Input.Version"] = version });
            Assert.Contains("Mill Street, 1962", await saved.Content.ReadAsStringAsync());

            await PostAsync(curator, id, "Publish", []);
            var published = await archive.WithDbAsync(db => db.Stories.SingleAsync(s => s.Id == id));
            Assert.Equal((StoryStatus.Published, "Mill Street, 1962", "A flood."), (published.Status, published.Title, published.Summary));
            Assert.NotNull(published.PublishedAt);

            await PostAsync(curator, id, "Unpublish", []);
            Assert.Equal(StoryStatus.Draft, (await archive.WithDbAsync(db => db.Stories.SingleAsync(s => s.Id == id))).Status);
            Assert.Equal([AuditAction.StoryCreated, AuditAction.StoryChanged, AuditAction.StoryPublished, AuditAction.StoryUnpublished], await ActionsAsync(archive, id));
        }
    }

    [Fact]
    public async Task A_title_is_needed_and_a_missing_story_answers_404()
    {
        var (archive, curator, _) = await StartAsync(app);
        await using (archive)
        using (curator)
        {
            var empty = await Browser.SendFormAsync(curator, "/Admin/Stories?handler=Create", new() { ["NewTitle"] = "  " }, "/Admin/Stories");
            Assert.Contains("Please enter a title.", await empty.Content.ReadAsStringAsync());
            Assert.Empty(await archive.WithDbAsync(db => db.Stories.ToListAsync()));
            Assert.Equal(HttpStatusCode.NotFound, (await curator.GetAsync(Page(999))).StatusCode);
            var id = await CreateAsync(curator);
            var missing = await Browser.SendFormAsync(curator, $"{Page(999)}?handler=AddText", new() { ["text"] = "x" }, Page(id));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(curator, id, "RemoveBlock", [], 12345)).StatusCode);
        }
    }

    [Fact]
    public async Task Text_and_items_are_added_by_number_and_by_address_and_keep_their_order()
    {
        var (archive, curator, items) = await StartAsync(app);
        await using (archive)
        using (curator)
        {
            var id = await CreateAsync(curator);
            await AddTextAsync(curator, id, "  First words  ");
            await AddItemAsync(curator, id, items.Published.ToString(), "The flood");
            await AddItemAsync(curator, id, $"/items/{items.Published}");
            await AddItemAsync(curator, id, $"https://archive.example/Admin/Items/Edit/{items.Draft}");
            var blocks = await BlocksAsync(archive, id);

            Assert.Equal([0, 1, 2, 3], blocks.Select(b => b.Position));
            Assert.Equal(("First words", null), (blocks[0].Text, blocks[0].ArchiveItemId));
            Assert.Equal((items.Published, "The flood"), (blocks[1].ArchiveItemId, blocks[1].Caption));
            Assert.Equal(items.Published, blocks[2].ArchiveItemId);
            Assert.Null(blocks[2].Caption);
            Assert.Equal(items.Draft, blocks[3].ArchiveItemId);
            var page = await curator.GetStringAsync(Page(id));
            Assert.Contains("Visitors do not see this item: it is not published.", page);
        }
    }

    [Fact]
    public async Task Unknown_binned_and_withdrawn_items_and_empty_or_long_text_are_refused()
    {
        var (archive, curator, items) = await StartAsync(app);
        await using (archive)
        using (curator)
        {
            var id = await CreateAsync(curator);
            var unknown = await (await AddItemAsync(curator, id, "987654")).Content.ReadAsStringAsync();
            var binned = await (await AddItemAsync(curator, id, items.Binned.ToString())).Content.ReadAsStringAsync();
            var withdrawn = await (await AddItemAsync(curator, id, items.Withdrawn.ToString())).Content.ReadAsStringAsync();
            var nonsense = await (await AddItemAsync(curator, id, "the flood")).Content.ReadAsStringAsync();
            var longCaption = await (await AddItemAsync(curator, id, items.Published.ToString(), new string('c', StoryBlock.CaptionMaxLength + 1))).Content.ReadAsStringAsync();
            var empty = await (await AddTextAsync(curator, id, "   ")).Content.ReadAsStringAsync();
            var longText = await (await AddTextAsync(curator, id, new string('t', StoryBlock.TextMaxLength + 1))).Content.ReadAsStringAsync();

            Assert.Contains("There is no item with this number.", unknown);
            Assert.Contains("This item is in the bin.", binned);
            Assert.Contains("turned down or withdrawn", withdrawn);
            Assert.Contains("Please enter the number of an item", nonsense);
            Assert.Contains("The caption can be at most", longCaption);
            Assert.Contains("Please write some text.", empty);
            Assert.Contains("The text can be at most", longText);
            Assert.Empty(await BlocksAsync(archive, id));
            Assert.Equal([AuditAction.StoryCreated], await ActionsAsync(archive, id));
        }
    }

    [Fact]
    public async Task A_story_holds_at_most_the_limit_of_parts()
    {
        var (archive, curator, items) = await StartAsync(app);
        await using (archive)
        using (curator)
        {
            var id = await CreateAsync(curator);
            await archive.WithDbAsync(async db =>
            {
                for (var i = 0; i < Story.MaxBlocks; i++)
                    db.StoryBlocks.Add(new StoryBlock { StoryId = id, Position = i, Kind = StoryBlockKind.Text, Text = $"Part {i}" });
                return await db.SaveChangesAsync();
            });

            var text = await (await AddTextAsync(curator, id, "one too many")).Content.ReadAsStringAsync();
            var item = await (await AddItemAsync(curator, id, items.Published.ToString())).Content.ReadAsStringAsync();

            Assert.Contains("A story can have at most 200 parts.", text);
            Assert.Contains("A story can have at most 200 parts.", item);
            Assert.Equal(Story.MaxBlocks, (await BlocksAsync(archive, id)).Count);
        }
    }

    [Fact]
    public async Task A_part_moves_earlier_and_later_and_stays_put_at_the_ends()
    {
        var (archive, curator, _) = await StartAsync(app);
        await using (archive)
        using (curator)
        {
            var id = await CreateAsync(curator);
            foreach (var word in new[] { "a", "b", "c" })
                await AddTextAsync(curator, id, word);
            async Task<string> OrderAsync() => string.Concat((await BlocksAsync(archive, id)).Select(b => b.Text));
            var blocks = await BlocksAsync(archive, id);

            await PostAsync(curator, id, "MoveBlock", new() { ["direction"] = "earlier" }, blocks[2].Id);
            Assert.Equal("acb", await OrderAsync());
            await PostAsync(curator, id, "MoveBlock", new() { ["direction"] = "later" }, blocks[0].Id);
            Assert.Equal("cab", await OrderAsync());
            await PostAsync(curator, id, "MoveBlock", new() { ["direction"] = "earlier" }, blocks[1].Id);
            Assert.Equal("cba", await OrderAsync());
            var first = (await BlocksAsync(archive, id))[0];
            await PostAsync(curator, id, "MoveBlock", new() { ["direction"] = "earlier" }, first.Id);
            await PostAsync(curator, id, "MoveBlock", new() { ["direction"] = "later" }, (await BlocksAsync(archive, id))[2].Id);
            Assert.Equal("cba", await OrderAsync());
            Assert.Equal([0, 1, 2], (await BlocksAsync(archive, id)).Select(b => b.Position));
        }
    }

    [Fact]
    public async Task A_part_of_another_story_answers_404_and_changes_nothing()
    {
        var (archive, curator, _) = await StartAsync(app);
        await using (archive)
        using (curator)
        {
            var mine = await CreateAsync(curator, "Mine");
            var other = await CreateAsync(curator, "Other");
            await AddTextAsync(curator, other, "theirs");
            var theirs = (await BlocksAsync(archive, other)).Single();

            var move = await PostAsync(curator, mine, "MoveBlock", new() { ["direction"] = "later" }, theirs.Id);
            var remove = await PostAsync(curator, mine, "RemoveBlock", [], theirs.Id);
            var save = await PostAsync(curator, mine, "SaveText", new() { ["text"] = "changed" }, theirs.Id);
            var caption = await PostAsync(curator, mine, "SaveCaption", new() { ["caption"] = "changed" }, theirs.Id);

            Assert.All(new[] { move, remove, save, caption }, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
            Assert.Equal("theirs", (await BlocksAsync(archive, other)).Single().Text);
        }
    }

    [Fact]
    public async Task Texts_and_captions_are_saved_and_a_part_is_removed_with_the_rest_renumbered()
    {
        var (archive, curator, items) = await StartAsync(app);
        await using (archive)
        using (curator)
        {
            var id = await CreateAsync(curator);
            await AddTextAsync(curator, id, "a");
            await AddItemAsync(curator, id, items.Published.ToString(), "old");
            await AddTextAsync(curator, id, "c");
            var blocks = await BlocksAsync(archive, id);

            await PostAsync(curator, id, "SaveText", new() { ["text"] = "A changed" }, blocks[0].Id);
            await PostAsync(curator, id, "SaveCaption", new() { ["caption"] = "new" }, blocks[1].Id);
            await PostAsync(curator, id, "RemoveBlock", [], blocks[1].Id);
            var after = await BlocksAsync(archive, id);

            Assert.Equal(["A changed", "c"], after.Select(b => b.Text));
            Assert.Equal([0, 1], after.Select(b => b.Position));
            Assert.True(await archive.WithDbAsync(db => db.ArchiveItems.AnyAsync(i => i.Id == items.Published)));
        }
    }

    [Fact]
    public async Task Moving_and_changing_parts_never_touch_the_story_itself()
    {
        var (archive, curator, items) = await StartAsync(app);
        await using (archive)
        using (curator)
        {
            var id = await CreateAsync(curator);
            await AddTextAsync(curator, id, "a");
            await AddItemAsync(curator, id, items.Published.ToString());
            var before = await archive.WithDbAsync(db => db.Stories.AsNoTracking().SingleAsync(s => s.Id == id));
            var blocks = await BlocksAsync(archive, id);

            await PostAsync(curator, id, "MoveBlock", new() { ["direction"] = "later" }, blocks[0].Id);
            await PostAsync(curator, id, "SaveText", new() { ["text"] = "b" }, blocks[0].Id);
            await AddTextAsync(curator, id, "c");
            await PostAsync(curator, id, "RemoveBlock", [], blocks[1].Id);
            var after = await archive.WithDbAsync(db => db.Stories.AsNoTracking().SingleAsync(s => s.Id == id));

            Assert.Equal((before.Version, before.UpdatedAt), (after.Version, after.UpdatedAt));
        }
    }

    [Fact]
    public async Task Someone_who_saves_over_a_newer_version_is_shown_the_other_version_first()
    {
        var (archive, curator, _) = await StartAsync(app);
        await using (archive)
        using (curator)
        {
            var id = await CreateAsync(curator);
            var form = await curator.GetStringAsync(Page(id));
            var version = Regex.Match(form, @"name=""Input.Version""[^>]*value=""(\d+)""").Groups[1].Value;
            await Browser.PostFormAsync(curator, Page(id), new() { ["Input.Title"] = "First save", ["Input.Version"] = version });

            var stale = await Browser.PostFormAsync(curator, Page(id), new() { ["Input.Title"] = "Second, from the old form", ["Input.Version"] = version }, Page(id));
            var html = await stale.Content.ReadAsStringAsync();

            Assert.Contains("Someone else changed this story", html);
            Assert.Contains("First save", html);
            Assert.Equal("First save", (await archive.WithDbAsync(db => db.Stories.SingleAsync(s => s.Id == id))).Title);
        }
    }

    [Fact]
    public async Task Every_change_is_in_the_activity_log_and_one_sitting_of_moves_is_one_entry()
    {
        var (archive, curator, items) = await StartAsync(app);
        await using (archive)
        using (curator)
        {
            var id = await CreateAsync(curator);
            await AddTextAsync(curator, id, "a");
            await AddItemAsync(curator, id, items.Published.ToString());
            var blocks = await BlocksAsync(archive, id);
            await PostAsync(curator, id, "SaveText", new() { ["text"] = "a2" }, blocks[0].Id);
            await PostAsync(curator, id, "MoveBlock", new() { ["direction"] = "later" }, blocks[0].Id);
            await PostAsync(curator, id, "MoveBlock", new() { ["direction"] = "earlier" }, blocks[0].Id);
            await PostAsync(curator, id, "MoveBlock", new() { ["direction"] = "later" }, blocks[0].Id);
            await PostAsync(curator, id, "RemoveBlock", [], blocks[1].Id);

            var actions = await ActionsAsync(archive, id);
            Assert.Equal([AuditAction.StoryCreated, AuditAction.StoryChanged, AuditAction.StoryItemAdded, AuditAction.StoryChanged,
                AuditAction.StoryBlocksArranged, AuditAction.StoryItemRemoved], actions);
            var activity = await curator.GetStringAsync("/Admin/Activity?what=Stories");
            Assert.Contains("Changed the order of the parts of the story", activity);
            Assert.Contains($"/Admin/Stories/Edit/{id}", activity);
            Assert.Contains("Stories</option>", await curator.GetStringAsync("/Admin/Activity"));
        }
    }

    [Fact]
    public async Task Stories_move_in_the_list_and_the_list_counts_what_visitors_would_not_see()
    {
        var (archive, curator, items) = await StartAsync(app);
        await using (archive)
        using (curator)
        {
            var first = await CreateAsync(curator, "First");
            var second = await CreateAsync(curator, "Second");
            await AddItemAsync(curator, first, items.Draft.ToString());
            await AddItemAsync(curator, first, items.Published.ToString());
            await AddTextAsync(curator, first, "words");

            var list = await curator.GetStringAsync("/Admin/Stories");
            Assert.Contains("1 item not shown to visitors", list);
            Assert.True(list.IndexOf("First", StringComparison.Ordinal) < list.IndexOf("Second", StringComparison.Ordinal));

            await Browser.PostFormAsync(curator, $"/Admin/Stories?handler=MoveEarlier&id={second}", [], "/Admin/Stories");
            Assert.Equal(["Second", "First"], await archive.WithDbAsync(db => db.Stories.OrderBy(s => s.Position).Select(s => s.Title).ToListAsync()));
            await Browser.PostFormAsync(curator, $"/Admin/Stories?handler=MoveEarlier&id={second}", [], "/Admin/Stories");
            Assert.Equal(["Second", "First"], await archive.WithDbAsync(db => db.Stories.OrderBy(s => s.Position).Select(s => s.Title).ToListAsync()));
            Assert.Contains(AuditAction.StoriesArranged, await ActionsAsync(archive, second));
        }
    }

    [Fact]
    public async Task The_preview_leaves_out_what_visitors_would_not_see()
    {
        var (archive, curator, items) = await StartAsync(app);
        await using (archive)
        using (curator)
        {
            var id = await CreateAsync(curator, "Preview me");
            await AddTextAsync(curator, id, "Opening words");
            await AddItemAsync(curator, id, items.Draft.ToString(), "Secret caption");
            await AddItemAsync(curator, id, items.Published.ToString(), "Shown caption");

            var preview = await curator.GetStringAsync($"/Admin/Stories/Preview/{id}");
            Assert.Contains("Opening words", preview);
            Assert.Contains("Shown caption", preview);
            Assert.DoesNotContain("Secret caption", preview);
            Assert.DoesNotContain("Draft flood", preview);

        }
    }

    [Fact]
    public async Task A_helper_is_shut_out_of_every_story_page_and_handler()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (helper, _, _, _) = await app.CreateAccountAsync(admin, Roles.Helper);
        var id = await CreateAsync(admin, Unique("Helper story"));
        var block = await app.WithDbAsync(async db =>
        {
            var added = new StoryBlock { StoryId = id, Position = 0, Kind = StoryBlockKind.Text, Text = "words" };
            db.StoryBlocks.Add(added);
            await db.SaveChangesAsync();
            return added.Id;
        });

        foreach (var url in new[] { "/Admin/Stories", Page(id), $"/Admin/Stories/Preview/{id}" })
            Assert.Equal(HttpStatusCode.Forbidden, (await helper.GetAsync(url)).StatusCode);
        var posts = new List<HttpResponseMessage>
        {
            await Browser.SendFormAsync(helper, "/Admin/Stories?handler=Create", new() { ["NewTitle"] = "x" }, "/Admin"),
            await Browser.SendFormAsync(helper, $"/Admin/Stories?handler=MoveEarlier&id={id}", [], "/Admin"),
            await Browser.SendFormAsync(helper, $"/Admin/Stories?handler=MoveLater&id={id}", [], "/Admin"),
            await Browser.SendFormAsync(helper, Page(id), new() { ["Input.Title"] = "x" }, "/Admin"),
        };
        foreach (var (handler, fields) in new (string, Dictionary<string, string>)[]
        {
            ("Publish", []), ("Unpublish", []), ("AddText", new() { ["text"] = "x" }), ("AddItem", new() { ["item"] = "1" }),
            ("SaveText", new() { ["text"] = "x" }), ("SaveCaption", new() { ["caption"] = "x" }),
            ("MoveBlock", new() { ["direction"] = "later" }), ("RemoveBlock", []),
        })
            posts.Add(await Browser.SendFormAsync(helper, $"{Page(id)}?handler={handler}&blockId={block}", fields, "/Admin"));

        Assert.All(posts, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        var stored = await app.WithDbAsync(db => db.Stories.Include(s => s.Blocks).SingleAsync(s => s.Id == id));
        Assert.Equal((StoryStatus.Draft, "words"), (stored.Status, Assert.Single(stored.Blocks).Text));
    }

    private static string Unique(string text) => TestData.Unique(text);
}
