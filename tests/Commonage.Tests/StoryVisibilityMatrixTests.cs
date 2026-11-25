using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

/// <summary>
/// What visitors read of a story, address by address. Item B has a word of its own in its title, description, credit, photo and
/// caption, so any part of it that shows where it must not is caught. Every test has an archive of its own, so that what is
/// listed and counted is only what the test made. See <c>docs/plan-m29-stories.md</c>.
/// </summary>
[Collection(nameof(AppCollection))]
public partial class StoryVisibilityMatrixTests(CommonageAppFactory app)
{
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Seeded(int StoryId, int AId, int BId, int CId, int BFileId, int BlockOfB, string BWord, string StoryWord);

    private static ArchiveItem NewItem(Collection collection, string title, DateTime now, bool withFile, string word)
    {
        var item = new ArchiveItem
        {
            Title = title, Description = $"Words {word}", Credit = $"Credit {word}", Collection = collection, CreatedAt = now, UpdatedAt = now,
        };
        if (withFile)
            item.MediaFiles.Add(new MediaFile
            {
                ArchiveItem = item, OriginalFileName = "a.jpg", ContentType = "image/jpeg", Sha256 = new string('a', 64),
                StorageId = Guid.NewGuid(), Status = ProcessingStatus.Ready, AltText = $"Alt {word}", Caption = $"Photo caption {word}",
            });
        return item;
    }

    /// <summary>A published story: text, A, B, C, text. A and C are visible; B is in the state given.</summary>
    private static async Task<Seeded> SeedAsync(WebApplicationFactory<Program> archive, string kind, Action<ArchiveItem, DateTime> stateOfB, bool publishStory = true, bool siblingsVisible = true)
    {
        var bWord = TestData.UniqueWord(kind);
        var storyWord = TestData.UniqueWord("story");
        return await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = $"Box {TestData.UniqueWord("box")}", CreatedAt = now };
            var a = NewItem(collection, $"Alpha {TestData.UniqueWord("a")}", now, true, "alpha");
            var b = NewItem(collection, $"Title {bWord}", now, true, bWord);
            var c = NewItem(collection, $"Charlie {TestData.UniqueWord("c")}", now, true, "charlie");
            if (siblingsVisible)
            {
                a.Publish(now);
                c.Publish(now);
            }
            stateOfB(b, now);
            var story = new Story { Title = $"Story {storyWord}", Summary = $"Summary {storyWord}", CreatedAt = now, UpdatedAt = now };
            if (publishStory)
                story.Publish(now);
            void Part(StoryBlockKind kind, string? text = null, ArchiveItem? item = null, string? caption = null) =>
                story.Blocks.Add(new StoryBlock { Position = story.Blocks.Count, Kind = kind, Text = text, ArchiveItem = item, Caption = caption });
            Part(StoryBlockKind.Text, $"Opening text {storyWord}");
            Part(StoryBlockKind.Item, item: a, caption: "Caption of the first");
            Part(StoryBlockKind.Item, item: b, caption: $"Caption {bWord}");
            Part(StoryBlockKind.Item, item: c, caption: "Caption of the third");
            Part(StoryBlockKind.Text, $"Closing text {storyWord}");
            db.AddRange(collection, a, b, c, story);
            await db.SaveChangesAsync();
            var blockOfB = story.Blocks.Single(x => x.ArchiveItem == b).Id;
            return new Seeded(story.Id, a.Id, b.Id, c.Id, b.MediaFiles.Single().Id, blockOfB, bWord, storyWord);
        });
    }

    private static async Task<WebApplicationFactory<Program>> NewArchiveAsync(CommonageAppFactory app, MutableClock? clock = null)
    {
        if (clock is null)
            return await app.NewArchiveAsync();
        return await app.NewArchiveAsync(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));
    }

    private static async Task<string> TextAsync(HttpClient client, string address)
    {
        var response = await client.GetAsync(address);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : "";
    }

    /// <summary>Every page carries a form token that is new with each response; nothing else is changed.</summary>
    private static string WithoutToken(string page) => Token().Replace(page, "$1");

    [GeneratedRegex(@"(__RequestVerificationToken[^>]*?value="")[^""]*")]
    private static partial Regex Token();

    private static string Headers(HttpResponseMessage response) => string.Join('\n',
        response.Headers.Concat(response.Content.Headers).Where(h => h.Key is not ("Date" or "Set-Cookie")).OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => $"{h.Key}: {string.Join(',', h.Value)}"));

    private static string MetaTags(string page) => string.Concat(Regex.Matches(page, @"<meta[^>]*(og:|name=""description"")[^>]*>").Select(m => m.Value + "\n"));

    /// <summary>The archive gets a logo, as most do: a page without a picture of its own then shows the logo, as on every other page.</summary>
    private static async Task<Guid> GiveLogoAsync(WebApplicationFactory<Program> archive)
    {
        var logo = Guid.NewGuid();
        await archive.WithDbAsync(async db =>
        {
            var settings = await db.ArchiveSettings.FirstOrDefaultAsync();
            if (settings is null)
                db.ArchiveSettings.Add(settings = new ArchiveSettings());
            settings.LogoId = logo;
            return await db.SaveChangesAsync();
        });
        archive.Services.GetRequiredService<Commonage.Web.Operations.ArchiveProfile>().Forget();
        return logo;
    }

    /// <summary>
    /// A link preview of a story has no picture of an item: its og:image is the archive logo or nothing, never a file under /media,
    /// and the large card is announced exactly when there is an image.
    /// </summary>
    private static void AssertNoItemPicture(string page, Guid logo)
    {
        var images = Regex.Matches(page, @"<meta property=""og:image"" content=""([^""]*)""").Select(m => m.Groups[1].Value).ToList();
        Assert.All(images, image =>
        {
            Assert.DoesNotContain("/media/", image);
            Assert.EndsWith($"/branding/{logo}/icon", image);
        });
        Assert.Equal(images.Count > 0, page.Contains("twitter:card"));
        Assert.DoesNotContain("og:image:alt", page);
    }

    /// <summary>The card of one story in a list, from its list item to the end of it.</summary>
    private static string Card(string page, int storyId)
    {
        var at = page.IndexOf($"/stories/{storyId}\"", StringComparison.Ordinal);
        Assert.True(at > 0, $"Story {storyId} is not listed.");
        var start = page.LastIndexOf("<li", at, StringComparison.Ordinal);
        return page[start..(page.IndexOf("</li>", at, StringComparison.Ordinal) + 5)];
    }

    private static string[] Addresses(Seeded s) =>
        ["/stories", $"/stories/{s.StoryId}", "/", "/sitemap.xml", "/feed.xml", $"/items/{s.BId}", $"/media/{s.BFileId}/thumbnail",
         $"/media/{s.BFileId}/card"];

    [Fact]
    public async Task An_item_block_in_a_state_visitors_may_not_see_leaves_no_trace_and_the_page_is_the_page_without_the_block()
    {
        var states = new Dictionary<string, Action<ArchiveItem, DateTime>>
        {
            ["draft"] = (_, _) => { },
            ["waiting"] = (i, _) => i.Status = ItemStatus.Submitted,
            ["rejected"] = (i, now) => i.Reject(now),
            ["withdrawn"] = (i, now) =>
            {
                i.Contribution = new Contribution
                {
                    ContributorName = "Someone", ShowName = false, Rights = RightsStatement.PermissionFromRightsHolder,
                    ConsentText = "I agree.", SubmittedAt = now,
                };
                i.Withdraw(now);
            },
            ["embargoed"] = (i, now) => { i.Publish(now); i.SetPublicFrom(DateOnly.FromDateTime(now).AddDays(3), now); },
            ["binned"] = (i, now) => { i.Publish(now); i.MoveToBin(now); },
        };
        await using var archive = await NewArchiveAsync(app);
        using var visitor = archive.CreateClient();

        foreach (var (kind, state) in states)
        {
            var seeded = await SeedAsync(archive, kind, state);
            var everything = new StringBuilder();
            foreach (var address in Addresses(seeded))
                everything.Append(await TextAsync(visitor, address));
            var text = everything.ToString();

            Assert.Contains($"Opening text {seeded.StoryWord}", text);
            Assert.Contains("Caption of the first", text);
            Assert.Contains("Caption of the third", text);
            Assert.True(!text.Contains(seeded.BWord), $"{kind}: a word of the hidden item shows.");
            Assert.DoesNotContain($"/items/{seeded.BId}\"", text);
            Assert.DoesNotContain($"/items/{seeded.BId}<", text);
            Assert.DoesNotContain($"/media/{seeded.BFileId}/", text);
            // The search page repeats what was typed, so it is checked for what it would show of the item, not for the word.
            var search = await TextAsync(visitor, $"/search?q={seeded.BWord}");
            Assert.DoesNotContain($"Title {seeded.BWord}", search);
            Assert.DoesNotContain($"/items/{seeded.BId}\"", search);
            foreach (var variant in new[] { "preview", "thumbnail", "card", "large" })
                Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{seeded.BFileId}/{variant}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/items/{seeded.BId}")).StatusCode);

            // The block sits between two visible ones. With it taken out, the page is the same, byte for byte, but for the token.
            var withBlock = WithoutToken(await TextAsync(visitor, $"/stories/{seeded.StoryId}"));
            await archive.WithDbAsync(db => db.StoryBlocks.Where(b => b.Id == seeded.BlockOfB).ExecuteDeleteAsync());
            var withoutBlock = WithoutToken(await TextAsync(visitor, $"/stories/{seeded.StoryId}"));
            Assert.True(withBlock == withoutBlock, $"{kind}: the page tells that a block was left out.");
        }
    }

    [Fact]
    public async Task The_page_of_a_story_carries_no_id_position_anchor_or_data_attribute_of_its_blocks()
    {
        await using var archive = await NewArchiveAsync(app);
        var seeded = await SeedAsync(archive, "plain", (i, now) => i.Publish(now));
        using var visitor = archive.CreateClient();

        var body = await TextAsync(visitor, $"/stories/{seeded.StoryId}");
        var article = body[body.IndexOf("<article class=\"story\"", StringComparison.Ordinal)..body.IndexOf("</article>", StringComparison.Ordinal)];

        Assert.DoesNotContain(" id=\"", article);
        Assert.DoesNotContain("data-", article);
        Assert.DoesNotContain("href=\"#", article);
        Assert.DoesNotContain("style=", article);
    }

    [Fact]
    public async Task A_record_only_item_shows_its_title_and_link_but_no_picture()
    {
        await using var archive = await NewArchiveAsync(app);
        var seeded = await SeedAsync(archive, "recordonly", (i, now) => { i.Publish(now); i.MediaLevel = MediaLevel.RecordOnly; });
        using var visitor = archive.CreateClient();

        var story = await TextAsync(visitor, $"/stories/{seeded.StoryId}");
        var cards = await TextAsync(visitor, "/stories");

        Assert.Contains($"Title {seeded.BWord}", story);
        Assert.Contains($"/items/{seeded.BId}\"", story);
        Assert.Contains($"Caption {seeded.BWord}", story);
        foreach (var page in new[] { story, cards })
        {
            Assert.DoesNotContain($"/media/{seeded.BFileId}/", page);
            Assert.DoesNotContain($"Alt {seeded.BWord}", page);
        }
        foreach (var variant in new[] { "preview", "thumbnail", "card", "large" })
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/media/{seeded.BFileId}/{variant}")).StatusCode);
    }

    [Fact]
    public async Task A_contributor_who_did_not_agree_to_sharing_beyond_the_website_has_nothing_in_a_preview_or_the_feed()
    {
        await using var archive = await NewArchiveAsync(app);
        var logo = await GiveLogoAsync(archive);
        var seeded = await SeedAsync(archive, "notsharing", (i, now) =>
        {
            i.Publish(now);
            i.Contribution = new Contribution
            {
                ContributorName = "Erika Muster", ShowName = false, Rights = RightsStatement.PermissionFromRightsHolder,
                ConsentText = "I agree.", SubmittedAt = now, ConsentCoversSharing = false,
            };
        });
        using var visitor = archive.CreateClient();

        var story = await TextAsync(visitor, $"/stories/{seeded.StoryId}");

        // On the website the item shows as it does anywhere else.
        Assert.Contains($"Title {seeded.BWord}", story);
        Assert.DoesNotContain(seeded.BWord, MetaTags(story));
        AssertNoItemPicture(story, logo);
        Assert.DoesNotContain(seeded.BWord, await TextAsync(visitor, "/feed.xml"));
        Assert.DoesNotContain("Erika", story);
    }

    [Fact]
    public async Task The_preview_of_a_story_has_its_title_and_summary_and_no_item_picture_and_no_description_without_a_summary()
    {
        await using var archive = await NewArchiveAsync(app);
        var logo = await GiveLogoAsync(archive);
        var seeded = await SeedAsync(archive, "preview", (i, now) => i.Publish(now));
        var without = await archive.WithDbAsync(async db =>
        {
            var story = await db.Stories.SingleAsync(s => s.Id == seeded.StoryId);
            var copy = new Story { Title = "Without summary", Summary = null, CreatedAt = story.CreatedAt, UpdatedAt = story.UpdatedAt, Position = 1 };
            copy.Publish(story.CreatedAt);
            copy.Blocks.Add(new StoryBlock { Position = 0, Kind = StoryBlockKind.Text, Text = $"The first words {seeded.StoryWord}" });
            db.Stories.Add(copy);
            await db.SaveChangesAsync();
            return copy.Id;
        });
        using var visitor = archive.CreateClient();

        var with = MetaTags(await TextAsync(visitor, $"/stories/{seeded.StoryId}"));
        Assert.Contains($"content=\"Story {seeded.StoryWord}\"", with);
        Assert.Contains($"og:description\" content=\"Summary {seeded.StoryWord}\"", with);
        AssertNoItemPicture(await TextAsync(visitor, $"/stories/{seeded.StoryId}"), logo);
        Assert.Contains($"/branding/{logo}/icon", with);
        Assert.DoesNotContain(seeded.BWord, with);
        Assert.DoesNotContain("Alpha", with);

        var empty = MetaTags(await TextAsync(visitor, $"/stories/{without}"));
        Assert.Contains("og:title\" content=\"Without summary\"", empty);
        Assert.DoesNotContain("description", empty);
        Assert.DoesNotContain(seeded.StoryWord, empty);
        AssertNoItemPicture(await TextAsync(visitor, $"/stories/{without}"), logo);
    }

    [Fact]
    public async Task The_words_of_a_story_are_not_in_search_or_the_feed_and_the_story_is_in_the_sitemap_without_a_date()
    {
        await using var archive = await NewArchiveAsync(app);
        var seeded = await SeedAsync(archive, "words", (i, now) => i.Publish(now));
        using var visitor = archive.CreateClient();

        var search = await TextAsync(visitor, $"/search?q={seeded.StoryWord}");
        Assert.DoesNotContain($"Opening text {seeded.StoryWord}", search);
        Assert.DoesNotContain($"/stories/{seeded.StoryId}", search);
        var feed = await TextAsync(visitor, "/feed.xml");
        Assert.DoesNotContain(seeded.StoryWord, feed);
        Assert.DoesNotContain("/stories", feed);

        var sitemap = await TextAsync(visitor, "/sitemap.xml");
        Assert.Matches($"<url><loc>https?://[^<]+/stories/{seeded.StoryId}</loc></url>", sitemap);
        Assert.Matches("<url><loc>https?://[^<]+/stories</loc></url>", sitemap);

        var response = await visitor.GetAsync($"/stories/{seeded.StoryId}");
        Assert.False(response.Headers.Contains("ETag") || response.Content.Headers.Contains("Last-Modified"));
    }

    [Fact]
    public async Task A_story_that_is_a_draft_in_the_bin_all_hidden_or_unknown_answers_the_same_404_and_changes_nothing_else()
    {
        await using var archive = await NewArchiveAsync(app);
        var shown = await SeedAsync(archive, "shown", (i, now) => i.Publish(now));
        using var visitor = archive.CreateClient();
        var pagesBefore = new[] { "/stories", "/", "/sitemap.xml" };
        var before = new List<string>();
        foreach (var address in pagesBefore)
            before.Add(WithoutToken(await TextAsync(visitor, address)));

        // Their items are hidden too: a visible item would show on the start page and in the sitemap, whatever the story is.
        var draft = await SeedAsync(archive, "draftstory", (_, _) => { }, publishStory: false, siblingsVisible: false);
        var binned = await SeedAsync(archive, "binnedstory", (_, _) => { }, siblingsVisible: false);
        await archive.WithDbAsync(async db =>
        {
            var story = await db.Stories.SingleAsync(s => s.Id == binned.StoryId);
            story.MoveToBin(DateTime.UtcNow);
            return await db.SaveChangesAsync();
        });
        var allHidden = await SeedAsync(archive, "allhidden", (_, _) => { }, siblingsVisible: false);
        await archive.WithDbAsync(async db =>
        {
            // Only item blocks, and none of them visible: the first and the last are texts in the seed, so those go.
            await db.StoryBlocks.Where(b => b.StoryId == allHidden.StoryId && b.Kind == StoryBlockKind.Text).ExecuteDeleteAsync();
            await db.StoryBlocks.Where(b => b.StoryId == allHidden.StoryId && b.ArchiveItemId != allHidden.BId)
                .ExecuteDeleteAsync();
            return 0;
        });
        var embargoedOnly = await SeedAsync(archive, "embargoedonly", (i, now) => { i.Publish(now); i.SetPublicFrom(DateOnly.FromDateTime(now).AddDays(3), now); }, siblingsVisible: false);
        await archive.WithDbAsync(async db =>
        {
            await db.StoryBlocks.Where(b => b.StoryId == embargoedOnly.StoryId && b.ArchiveItemId != embargoedOnly.BId).ExecuteDeleteAsync();
            return 0;
        });
        var hidden = new[] { draft, binned, allHidden, embargoedOnly };

        var reference = await visitor.GetAsync("/stories/99999999");
        Assert.Equal(HttpStatusCode.NotFound, reference.StatusCode);
        var referenceBody = WithoutToken(await reference.Content.ReadAsStringAsync());
        foreach (var story in hidden)
        {
            var response = await visitor.GetAsync($"/stories/{story.StoryId}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(Headers(reference), Headers(response));
            Assert.Equal(referenceBody, WithoutToken(await response.Content.ReadAsStringAsync()));
        }

        // The listings are byte for byte what they were before the hidden stories existed.
        for (var i = 0; i < pagesBefore.Length; i++)
            Assert.Equal(before[i], WithoutToken(await TextAsync(visitor, pagesBefore[i])));
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/stories/{shown.StoryId}")).StatusCode);
    }

    [Fact]
    public async Task A_story_whose_only_item_is_embargoed_is_not_there_until_the_day_and_then_it_is()
    {
        var clock = new MutableClock(new DateTimeOffset(2031, 5, 9, 12, 0, 0, TimeSpan.Zero));
        await using var archive = await NewArchiveAsync(app, clock);
        var id = await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var item = NewItem(new Collection { Title = "Box", CreatedAt = now }, "Opening day", now, false, "x");
            item.Publish(now);
            item.SetPublicFrom(new DateOnly(2031, 5, 10), now);
            var story = new Story { Title = "Waiting story", CreatedAt = now, UpdatedAt = now };
            story.Publish(now);
            story.Blocks.Add(new StoryBlock { Position = 0, Kind = StoryBlockKind.Item, ArchiveItem = item, Caption = "Opening caption" });
            db.AddRange(item, story);
            await db.SaveChangesAsync();
            return story.Id;
        });
        using var visitor = archive.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/stories/{id}")).StatusCode);
        Assert.DoesNotContain("Waiting story", await TextAsync(visitor, "/stories"));
        Assert.DoesNotContain($"/stories/{id}<", await TextAsync(visitor, "/sitemap.xml"));

        clock.Now = new DateTimeOffset(2031, 5, 10, 12, 0, 0, TimeSpan.Zero);

        Assert.Contains("Opening caption", await TextAsync(visitor, $"/stories/{id}"));
        Assert.Contains("Waiting story", await TextAsync(visitor, "/stories"));
        Assert.Contains($"/stories/{id}<", await TextAsync(visitor, "/sitemap.xml"));
    }

    [Fact]
    public async Task A_story_follows_its_story_and_its_items_when_they_are_published_unpublished_binned_restored_and_deleted()
    {
        await using var archive = await NewArchiveAsync(app);
        var seeded = await SeedAsync(archive, "changing", (i, now) => i.Publish(now));
        using var visitor = archive.CreateClient();
        Task<string> StoryAsync() => TextAsync(visitor, $"/stories/{seeded.StoryId}");
        async Task ChangeAsync(Action<CommonageDbContextAccess> change)
        {
            await archive.WithDbAsync(async db =>
            {
                change(new CommonageDbContextAccess(db));
                return await db.SaveChangesAsync();
            });
        }

        Assert.Contains($"Title {seeded.BWord}", await StoryAsync());

        // The story goes back to a draft, and comes back.
        await ChangeAsync(a => a.Story(seeded.StoryId).Unpublish());
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/stories/{seeded.StoryId}")).StatusCode);
        await ChangeAsync(a => a.Story(seeded.StoryId).Publish(DateTime.UtcNow));
        Assert.Contains($"Title {seeded.BWord}", await StoryAsync());

        // The item goes to the bin: its block is gone; restoring the item brings the block back.
        await ChangeAsync(a => a.Item(seeded.BId).MoveToBin(DateTime.UtcNow));
        Assert.DoesNotContain(seeded.BWord, await StoryAsync());
        await ChangeAsync(a => a.Item(seeded.BId).RestoreFromBin(DateTime.UtcNow));
        Assert.Contains($"Caption {seeded.BWord}", await StoryAsync());

        // The story goes to the bin and is restored.
        await ChangeAsync(a => a.Story(seeded.StoryId).MoveToBin(DateTime.UtcNow));
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/stories/{seeded.StoryId}")).StatusCode);
        await ChangeAsync(a => a.Story(seeded.StoryId).RestoreFromBin(DateTime.UtcNow));
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/stories/{seeded.StoryId}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_an_item_for_good_leaves_the_story_with_its_other_blocks_in_order()
    {
        await using var archive = await NewArchiveAsync(app);
        var seeded = await SeedAsync(archive, "deleted", (i, now) => i.Publish(now));
        using var visitor = archive.CreateClient();

        await archive.WithDbAsync(async db =>
        {
            await db.MediaFiles.IgnoreQueryFilters().Where(f => f.ArchiveItemId == seeded.BId).ExecuteDeleteAsync();
            return await db.ArchiveItems.IgnoreQueryFilters().Where(i => i.Id == seeded.BId).ExecuteDeleteAsync();
        });

        var page = await TextAsync(visitor, $"/stories/{seeded.StoryId}");
        Assert.DoesNotContain(seeded.BWord, page);
        var order = new[] { $"Opening text {seeded.StoryWord}", "Caption of the first", "Caption of the third", $"Closing text {seeded.StoryWord}" };
        var places = order.Select(o => page.IndexOf(o, StringComparison.Ordinal)).ToArray();
        Assert.All(places, p => Assert.True(p > 0));
        Assert.Equal(places.Order(), places);
        var blocks = await archive.WithDbAsync(db => db.StoryBlocks.Where(b => b.StoryId == seeded.StoryId).CountAsync());
        Assert.Equal(4, blocks);
    }

    [Fact]
    public async Task The_list_pages_the_visible_stories_only_and_takes_each_cover_from_a_visible_item()
    {
        await using var archive = await NewArchiveAsync(app);
        var hiddenFirst = await SeedAsync(archive, "coverhidden", (_, _) => { });
        var visible = await SeedAsync(archive, "covervisible", (i, now) => i.Publish(now));
        using var visitor = archive.CreateClient();

        var list = await TextAsync(visitor, "/stories");
        var start = await TextAsync(visitor, "/");

        // The cover of a story is the picture of its first visible item (A), never of the hidden B before it in rank.
        foreach (var page in new[] { list, start })
        {
            Assert.Contains($"/stories/{visible.StoryId}\"", page);
            Assert.DoesNotContain(hiddenFirst.BWord, page);
            Assert.DoesNotContain($"/media/{hiddenFirst.BFileId}/", page);
        }
        Assert.Contains("<section class=\"home-section\" aria-labelledby=\"stories-heading\"", start);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/stories?p=999")).StatusCode);
    }

    private sealed record CoverSeed(int StoryId, int BFileId, int? AFileId, string BWord);

    /// <summary>A published story whose item blocks come in this order: B (in the state given), then A, a visible item, if asked for. A text only if asked for.</summary>
    private static async Task<CoverSeed> SeedCoverAsync(WebApplicationFactory<Program> archive, string kind, Action<ArchiveItem, DateTime> stateOfB, bool withA, bool withText)
    {
        var bWord = TestData.UniqueWord(kind);
        return await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = $"Box {TestData.UniqueWord("box")}", CreatedAt = now };
            var b = NewItem(collection, $"Title {bWord}", now, true, bWord);
            stateOfB(b, now);
            var a = NewItem(collection, $"Alpha {TestData.UniqueWord("a")}", now, true, "alpha");
            a.Publish(now);
            var story = new Story { Title = $"Cover {bWord}", CreatedAt = now, UpdatedAt = now };
            story.Publish(now);
            story.Blocks.Add(new StoryBlock { Position = 0, Kind = StoryBlockKind.Item, ArchiveItem = b });
            if (withA)
                story.Blocks.Add(new StoryBlock { Position = 1, Kind = StoryBlockKind.Item, ArchiveItem = a });
            if (withText)
                story.Blocks.Add(new StoryBlock { Position = 2, Kind = StoryBlockKind.Text, Text = $"Only words {bWord}" });
            db.AddRange(collection, b, a, story);
            await db.SaveChangesAsync();
            return new CoverSeed(story.Id, b.MediaFiles.Single().Id, withA ? a.MediaFiles.Single().Id : null, bWord);
        });
    }

    private static Dictionary<string, Action<ArchiveItem, DateTime>> StatesWithoutPicture() => new()
    {
        ["draft"] = (_, _) => { },
        ["embargoed"] = (i, now) => { i.Publish(now); i.SetPublicFrom(DateOnly.FromDateTime(now).AddDays(3), now); },
        ["binned"] = (i, now) => { i.Publish(now); i.MoveToBin(now); },
        ["recordonly"] = (i, now) => { i.Publish(now); i.MediaLevel = MediaLevel.RecordOnly; },
    };

    [Fact]
    public async Task The_cover_is_the_picture_of_the_first_item_visitors_may_see_even_when_a_hidden_or_record_only_item_comes_first()
    {
        await using var archive = await NewArchiveAsync(app);
        using var visitor = archive.CreateClient();
        var seeds = new List<(string Kind, CoverSeed Seed)>();
        foreach (var (kind, state) in StatesWithoutPicture())
            seeds.Add((kind, await SeedCoverAsync(archive, kind, state, withA: true, withText: false)));
        // Positive case: the first item is visible and has a picture, and it is the cover.
        var visible = await SeedCoverAsync(archive, "visiblefirst", (i, now) => i.Publish(now), withA: true, withText: false);

        var list = await TextAsync(visitor, "/stories");
        var start = await TextAsync(visitor, "/");
        foreach (var page in new[] { list, start })
        {
            foreach (var (kind, seed) in seeds)
            {
                Assert.True(!page.Contains($"/media/{seed.BFileId}/"), $"{kind}: the picture of the first item shows.");
                // The start page shows the first three stories only; the list shows all of them.
                if (page == list || page.Contains($"/stories/{seed.StoryId}\""))
                    Assert.Contains($"/media/{seed.AFileId}/thumbnail", Card(page, seed.StoryId));
            }
            if (page == list || page.Contains($"/stories/{visible.StoryId}\""))
                Assert.Contains($"/media/{visible.BFileId}/thumbnail", Card(page, visible.StoryId));
        }
    }

    [Fact]
    public async Task A_story_whose_only_item_is_hidden_or_record_only_is_kept_by_a_text_and_its_cover_is_the_text_tile()
    {
        await using var archive = await NewArchiveAsync(app);
        using var visitor = archive.CreateClient();
        var seeds = new List<(string Kind, CoverSeed Seed)>();
        foreach (var (kind, state) in StatesWithoutPicture())
            seeds.Add((kind, await SeedCoverAsync(archive, kind, state, withA: false, withText: true)));

        var list = await TextAsync(visitor, "/stories");
        var start = await TextAsync(visitor, "/");
        foreach (var page in new[] { list, start })
            foreach (var (kind, seed) in seeds)
            {
                if (page == list || page.Contains($"/stories/{seed.StoryId}\""))
                {
                    var card = Card(page, seed.StoryId);
                    Assert.True(card.Contains("text-tile") && !card.Contains("<img"), $"{kind}: the cover is not the text tile.");
                }
                Assert.True(!page.Contains($"/media/{seed.BFileId}/"), $"{kind}: the picture of the item shows.");
            }
    }

    [Fact]
    public async Task Hidden_stories_change_neither_the_pages_nor_the_totals_of_the_list()
    {
        await using var archive = await NewArchiveAsync(app);
        const int visibleStories = Commonage.Web.Search.StoryCards.PageSize + 2;
        await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            for (var n = 0; n < visibleStories; n++)
            {
                var story = new Story { Title = $"Paged {n:00}", CreatedAt = now, UpdatedAt = now, Position = n };
                story.Publish(now);
                story.Blocks.Add(new StoryBlock { Position = 0, Kind = StoryBlockKind.Text, Text = $"Words of {n}" });
                db.Stories.Add(story);
            }
            return await db.SaveChangesAsync();
        });
        using var visitor = archive.CreateClient();
        async Task<string[]> PagesAsync() =>
        [
            WithoutToken(await TextAsync(visitor, "/stories")), WithoutToken(await TextAsync(visitor, "/stories?p=2")),
            WithoutToken(await TextAsync(visitor, "/")), WithoutToken(await TextAsync(visitor, "/sitemap.xml")),
        ];
        static int Cards(string page) => Regex.Matches(page, "class=\"collection-card\"").Count;

        var before = await PagesAsync();
        Assert.Equal(Commonage.Web.Search.StoryCards.PageSize, Cards(before[0]));
        Assert.Equal(2, Cards(before[1]));

        // A draft, a story in the bin and one whose only item is hidden and first in the order. Their items are hidden too,
        // because a visible item would show on the start page and in the sitemap whatever the story is.
        await SeedAsync(archive, "pagedraft", (_, _) => { }, publishStory: false, siblingsVisible: false);
        var binned = await SeedAsync(archive, "pagebinned", (_, _) => { }, siblingsVisible: false);
        var onlyHidden = await SeedAsync(archive, "pagehidden", (_, _) => { }, siblingsVisible: false);
        await archive.WithDbAsync(async db =>
        {
            await db.StoryBlocks.Where(b => b.StoryId == onlyHidden.StoryId && b.ArchiveItemId != onlyHidden.BId).ExecuteDeleteAsync();
            await db.Stories.Where(s => s.Id == onlyHidden.StoryId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Position, -1));
            var story = await db.Stories.SingleAsync(s => s.Id == binned.StoryId);
            story.MoveToBin(DateTime.UtcNow);
            return await db.SaveChangesAsync();
        });

        var after = await PagesAsync();
        for (var i = 0; i < before.Length; i++)
            Assert.True(before[i] == after[i], $"Page {i} changed with hidden stories.");
    }

    [Fact]
    public async Task A_text_of_nothing_but_white_space_does_not_keep_a_story_visible_or_show_up_in_it()
    {
        await using var archive = await NewArchiveAsync(app);
        var ids = await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            Story Make(string title, params string[] texts)
            {
                var story = new Story { Title = title, CreatedAt = now, UpdatedAt = now };
                story.Publish(now);
                foreach (var text in texts)
                    story.Blocks.Add(new StoryBlock { Position = story.Blocks.Count, Kind = StoryBlockKind.Text, Text = text });
                return story;
            }
            var onlyBlank = Make("Blank only", "   ", "\n\t \r\n");
            var withWords = Make("Blank and words", " ", "Real words here");
            var hiddenItem = NewItem(new Collection { Title = "Box", CreatedAt = now }, "Hidden one", now, false, "hid");
            var blankAndHidden = Make("Blank and hidden item", "  ");
            blankAndHidden.Blocks.Add(new StoryBlock { Position = 1, Kind = StoryBlockKind.Item, ArchiveItem = hiddenItem });
            db.AddRange(onlyBlank, withWords, hiddenItem, blankAndHidden);
            await db.SaveChangesAsync();
            return (OnlyBlank: onlyBlank.Id, WithWords: withWords.Id, BlankAndHidden: blankAndHidden.Id);
        });
        using var visitor = archive.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/stories/{ids.OnlyBlank}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/stories/{ids.BlankAndHidden}")).StatusCode);
        var list = await TextAsync(visitor, "/stories");
        Assert.DoesNotContain("Blank only", list);
        Assert.DoesNotContain("Blank and hidden item", list);
        Assert.Contains("Blank and words", list);
        var sitemap = await TextAsync(visitor, "/sitemap.xml");
        Assert.DoesNotContain($"/stories/{ids.OnlyBlank}<", sitemap);
        Assert.DoesNotContain($"/stories/{ids.BlankAndHidden}<", sitemap);

        var page = await TextAsync(visitor, $"/stories/{ids.WithWords}");
        Assert.Contains("Real words here", page);
        var article = page[page.IndexOf("<article class=\"story\"", StringComparison.Ordinal)..page.IndexOf("</article>", StringComparison.Ordinal)];
        Assert.DoesNotMatch(@"<p[^>]*>\s*</p>", article);
    }

    /// <summary>Loads a story or an item for a test that changes it.</summary>
    private sealed class CommonageDbContextAccess(Commonage.Web.Data.CommonageDbContext db)
    {
        public Story Story(int id) => db.Stories.IgnoreQueryFilters().Single(s => s.Id == id);
        public ArchiveItem Item(int id) => db.ArchiveItems.IgnoreQueryFilters().Single(i => i.Id == id);
    }
}
