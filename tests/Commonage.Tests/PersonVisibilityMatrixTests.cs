using System.Net;
using System.Text.RegularExpressions;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

/// <summary>
/// What visitors read of a person, address by address and channel by channel. Each person has a word of their own in every name, in
/// the biography and in the note, so any part that shows where it must not is found by searching the answers. Every test has an
/// archive of its own. See <c>docs/plan-m30-people.md</c>.
/// </summary>
[Collection(nameof(AppCollection))]
public partial class PersonVisibilityMatrixTests(CommonageAppFactory app)
{
    private sealed record Seeded(
        PersonWords Deceased, string DeceasedKey, PersonWords Living, string LivingKey, PersonWords Hidden, string HiddenKey,
        PersonWords Binned, string BinnedKey, PersonWords OnlyDraft, string OnlyDraftKey, int PublicItemId, int SecondItemId, int DraftItemId);

    private static async Task<Seeded> SeedAsync(WebApplicationFactory<Program> archive)
    {
        var (deceased, living, hidden, binned, onlyDraft) = (PersonWords.New("d"), PersonWords.New("l"), PersonWords.New("h"), PersonWords.New("b"), PersonWords.New("x"));
        return await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = $"Box {TestData.UniqueWord("box")}", CreatedAt = now };
            var one = PeopleSeed.NewItem(collection, $"First {TestData.UniqueWord("one")}", now, published: true, withFile: true);
            var two = PeopleSeed.NewItem(collection, $"Second {TestData.UniqueWord("two")}", now, published: true);
            var draft = PeopleSeed.NewItem(collection, $"Draft {TestData.UniqueWord("draft")}", now, published: false);
            var pDeceased = PeopleSeed.NewPerson(deceased, PersonDisclosure.Deceased, now);
            var pLiving = PeopleSeed.NewPerson(living, PersonDisclosure.MayBeShown, now);
            var pHidden = PeopleSeed.NewPerson(hidden, PersonDisclosure.Hidden, now);
            var pBinned = PeopleSeed.NewPerson(binned, PersonDisclosure.Deceased, now);
            pBinned.MoveToBin(now);
            var pDraft = PeopleSeed.NewPerson(onlyDraft, PersonDisclosure.Deceased, now);
            db.AddRange(collection, one, two, draft, pDeceased, pLiving, pHidden, pBinned, pDraft);
            // The hidden person is on two visible items; the one of the draft only on a draft.
            db.AddRange(
                PeopleSeed.Link(one, pDeceased, now), PeopleSeed.Link(one, pLiving, now), PeopleSeed.Link(one, pHidden, now), PeopleSeed.Link(two, pHidden, now),
                PeopleSeed.Link(one, pBinned, now), PeopleSeed.Link(draft, pDraft, now));
            await db.SaveChangesAsync();
            return new Seeded(deceased, pDeceased.PublicId, living, pLiving.PublicId, hidden, pHidden.PublicId, binned, pBinned.PublicId, onlyDraft, pDraft.PublicId, one.Id, two.Id, draft.Id);
        });
    }

    private static async Task<Dictionary<string, string>> ChannelsAsync(HttpClient visitor, Seeded s) => new()
    {
        ["people"] = await PeopleSeed.TextAsync(visitor, "/people"),
        ["home"] = await PeopleSeed.TextAsync(visitor, "/"),
        ["item1"] = await PeopleSeed.TextAsync(visitor, $"/items/{s.PublicItemId}"),
        ["item2"] = await PeopleSeed.TextAsync(visitor, $"/items/{s.SecondItemId}"),
        ["json1"] = await PeopleSeed.TextAsync(visitor, $"/items/{s.PublicItemId}.json"),
        ["json2"] = await PeopleSeed.TextAsync(visitor, $"/items/{s.SecondItemId}.json"),
        ["feed"] = await PeopleSeed.TextAsync(visitor, "/feed.xml"),
        ["sitemap"] = await PeopleSeed.TextAsync(visitor, "/sitemap.xml"),
        ["search"] = await PeopleSeed.TextAsync(visitor, "/search"),
        ["places"] = await PeopleSeed.TextAsync(visitor, "/places"),
    };

    private static IEnumerable<string> ItemIdsIn(string page) => Regex.Matches(page, @"href=""/items/(\d+)""").Select(m => m.Groups[1].Value);

    [Fact]
    public async Task Nothing_of_a_hidden_binned_or_draft_only_person_is_on_any_public_page()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        using var visitor = archive.CreateClient();
        var s = await SeedAsync(archive);
        var channels = await ChannelsAsync(visitor, s);
        foreach (var word in new[] { s.Hidden, s.Binned, s.OnlyDraft }.SelectMany(w => w.Texts).Where(t => t != "1901"))
        {
            foreach (var (_, page) in channels)
                Assert.DoesNotContain(word, page);
            // And the search does not find an item by the words of a person nobody may see.
            Assert.DoesNotContain(s.PublicItemId.ToString(), ItemIdsIn(await PeopleSeed.TextAsync(visitor, $"/search?q={Uri.EscapeDataString(word)}")));
        }
        foreach (var key in new[] { s.HiddenKey, s.BinnedKey, s.OnlyDraftKey })
        {
            foreach (var (_, page) in channels)
                Assert.DoesNotContain(key, page);
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/people/{key}")).StatusCode);
        }
    }

    [Fact]
    public async Task A_person_who_has_died_has_a_page_with_name_dates_biography_and_other_names_marked_for_visitors()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        using var visitor = archive.CreateClient();
        var s = await SeedAsync(archive);

        var response = await visitor.GetAsync($"/people/{s.DeceasedKey}");
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(s.Deceased.Name, page);
        Assert.Contains(s.Deceased.Marked, page);
        Assert.Contains(s.Deceased.Biography, page);
        Assert.Contains(s.Deceased.Born, page);
        Assert.Contains($"/items/{s.PublicItemId}", page);
        Assert.DoesNotContain($"/items/{s.DraftItemId}", page);
        Assert.DoesNotContain(s.Deceased.Secret, page);
        Assert.DoesNotContain(s.Deceased.Note, page);
        Assert.DoesNotContain("noindex", page);
        Assert.False(response.Headers.Contains("X-Robots-Tag"));
        Assert.Null(response.Headers.ETag);
        Assert.Null(response.Content.Headers.LastModified);
    }

    [Fact]
    public async Task A_person_who_may_be_living_has_no_dates_and_is_kept_from_search_engines_by_header_and_page()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        using var visitor = archive.CreateClient();
        var s = await SeedAsync(archive);

        var response = await visitor.GetAsync($"/people/{s.LivingKey}");
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(s.Living.Name, page);
        Assert.Contains(s.Living.Biography, page);
        Assert.DoesNotContain(s.Living.Born, page);
        Assert.DoesNotContain("1980", page);
        Assert.DoesNotContain(s.Living.Secret, page);
        Assert.DoesNotContain(s.Living.Note, page);
        Assert.Equal("noindex", string.Join(',', response.Headers.GetValues("X-Robots-Tag")));
        Assert.Matches(@"<meta name=""robots"" content=""noindex", page);
    }

    [Fact]
    public async Task The_list_of_people_is_kept_from_search_engines_when_it_names_someone_who_may_be_living()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        using var visitor = archive.CreateClient();
        var s = await SeedAsync(archive);

        var response = await visitor.GetAsync("/people");
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(s.Living.Name, page);
        Assert.Equal("noindex", string.Join(',', response.Headers.GetValues("X-Robots-Tag")));
        Assert.Matches(@"<meta name=""robots"" content=""noindex", page);
    }

    [Fact]
    public async Task The_list_of_people_stays_indexable_while_it_names_only_people_who_have_died()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        using var visitor = archive.CreateClient();
        var now = DateTime.UtcNow;
        var words = PersonWords.New("only");
        await archive.WithDbAsync(async db =>
        {
            var collection = new Collection { Title = "Box", CreatedAt = now };
            var item = PeopleSeed.NewItem(collection, "Visible", now, true);
            db.AddRange(collection, item, PeopleSeed.Link(item, PeopleSeed.NewPerson(words, PersonDisclosure.Deceased, now), now));
            return await db.SaveChangesAsync();
        });

        var response = await visitor.GetAsync("/people");
        var page = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(words.Name, page);
        Assert.False(response.Headers.Contains("X-Robots-Tag"));
        Assert.DoesNotContain("noindex", page);
    }

    [Fact]
    public async Task PeopleInLinkPreview_a_person_who_has_died_has_their_name_and_one_who_may_be_living_the_title_of_the_archive()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        using var visitor = archive.CreateClient();
        var s = await SeedAsync(archive);
        await PeopleSeed.GiveLogoAsync(archive);

        var deceased = PeopleSeed.MetaTags(await PeopleSeed.TextAsync(visitor, $"/people/{s.DeceasedKey}"));
        var living = PeopleSeed.MetaTags(await PeopleSeed.TextAsync(visitor, $"/people/{s.LivingKey}"));

        Assert.Contains(s.Deceased.Name, deceased);
        Assert.DoesNotContain(s.Living.Name, living);
        Assert.DoesNotContain(s.Living.Biography, living);
        Assert.DoesNotContain(s.Living.Marked, living);
        Assert.Contains("og:title", living);
    }

    [Fact]
    public async Task The_people_line_of_an_item_has_the_names_of_public_people_and_links_by_key_only()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        using var visitor = archive.CreateClient();
        var s = await SeedAsync(archive);

        var one = await PeopleSeed.TextAsync(visitor, $"/items/{s.PublicItemId}");
        var two = await PeopleSeed.TextAsync(visitor, $"/items/{s.SecondItemId}");

        Assert.Contains($"href=\"/people/{s.DeceasedKey}\"", one);
        Assert.Contains($"href=\"/people/{s.LivingKey}\"", one);
        Assert.Contains(s.Deceased.Name, one);
        Assert.Contains(s.Living.Name, one);
        Assert.DoesNotContain(s.Deceased.Marked, one);
        // The second item has only a hidden person: no line at all.
        Assert.DoesNotContain("id=\"item-people\"", two);
    }

    [Fact]
    public async Task PeopleInSitemap_only_people_who_have_died_without_a_date_of_change()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        using var visitor = archive.CreateClient();
        var s = await SeedAsync(archive);

        var sitemap = await PeopleSeed.TextAsync(visitor, "/sitemap.xml");

        Assert.Matches($@"<url>\s*<loc>[^<]*/people/{s.DeceasedKey}</loc>\s*</url>", sitemap);
        Assert.DoesNotContain(s.LivingKey, sitemap);
        Assert.DoesNotContain(s.HiddenKey, sitemap);
        Assert.DoesNotContain(s.BinnedKey, sitemap);
        Assert.DoesNotContain(s.OnlyDraftKey, sitemap);
        Assert.Matches(@"<loc>[^<]*/people</loc>\s*</url>", sitemap);
    }

    [Fact]
    public async Task PeopleInSitemap_the_page_of_people_is_not_listed_while_nobody_who_has_died_is_public()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        using var visitor = archive.CreateClient();
        await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = "Box", CreatedAt = now };
            var item = PeopleSeed.NewItem(collection, "Plain", now, true);
            var living = PeopleSeed.NewPerson(PersonWords.New("n"), PersonDisclosure.MayBeShown, now);
            db.AddRange(collection, item, living, PeopleSeed.Link(item, living, now));
            return await db.SaveChangesAsync();
        });

        var sitemap = await PeopleSeed.TextAsync(visitor, "/sitemap.xml");

        Assert.DoesNotContain("/people", sitemap);
    }

    [Fact]
    public async Task PeopleNotInFeed_nobody_is_named_in_the_feed()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        using var visitor = archive.CreateClient();
        var s = await SeedAsync(archive);

        var feed = await PeopleSeed.TextAsync(visitor, "/feed.xml");

        Assert.Contains($"/items/{s.PublicItemId}", feed);
        foreach (var word in new[] { s.Deceased, s.Living }.SelectMany(w => w.Texts))
            Assert.DoesNotContain(word, feed);
        Assert.DoesNotContain("/people", feed);
    }

    [Fact]
    public async Task PeopleInItemJson_only_people_who_have_died_by_their_main_name()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        using var visitor = archive.CreateClient();
        var s = await SeedAsync(archive);

        var json = await PeopleSeed.TextAsync(visitor, $"/items/{s.PublicItemId}.json");

        Assert.Contains(s.Deceased.Name, json);
        Assert.DoesNotContain(s.Living.Name, json);
        Assert.DoesNotContain(s.Deceased.Marked, json);
        Assert.DoesNotContain(s.Deceased.Secret, json);
        Assert.DoesNotContain(s.DeceasedKey, json);
    }

    [Fact]
    public async Task PeopleInDublinCore_only_people_who_have_died_by_their_main_name()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        using var visitor = archive.CreateClient();
        var s = await SeedAsync(archive);

        var page = await PeopleSeed.TextAsync(visitor, $"/items/{s.PublicItemId}");

        Assert.Contains($"<meta name=\"DC.subject\" content=\"{s.Deceased.Name}\"", page);
        Assert.DoesNotContain($"content=\"{s.Living.Name}\"", page);
    }

    [Fact]
    public async Task PeopleInSearch_an_item_is_found_by_the_public_names_of_its_people_and_by_nothing_private()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        using var visitor = archive.CreateClient();
        var s = await SeedAsync(archive);

        async Task<bool> FindsAsync(string words) =>
            ItemIdsIn(await PeopleSeed.TextAsync(visitor, $"/search?q={Uri.EscapeDataString(words)}")).Contains(s.PublicItemId.ToString());

        Assert.True(await FindsAsync(s.Deceased.Name));
        Assert.True(await FindsAsync(s.Deceased.Marked));
        Assert.True(await FindsAsync(s.Living.Name));
        Assert.False(await FindsAsync(s.Deceased.Secret));
        Assert.False(await FindsAsync(s.Deceased.Note));
        Assert.False(await FindsAsync(s.Deceased.Biography));
        Assert.False(await FindsAsync(s.Hidden.Name));
        Assert.False(await FindsAsync(s.Binned.Name));
    }

    [Fact]
    public async Task A_change_of_choice_is_seen_by_the_next_visitor_and_an_embargo_that_ends_opens_the_person()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        await using var archive = await PeopleSeed.NewArchiveAsync(app, clock);
        using var visitor = archive.CreateClient();
        var words = PersonWords.New("t");
        var (personId, itemId) = await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = "Box", CreatedAt = now };
            var item = PeopleSeed.NewItem(collection, $"Embargoed {TestData.UniqueWord("e")}", now, true);
            item.SetPublicFrom(DateOnly.FromDateTime(now).AddDays(10), now);
            var person = PeopleSeed.NewPerson(words, PersonDisclosure.Deceased, now);
            db.AddRange(collection, item, person, PeopleSeed.Link(item, person, now));
            await db.SaveChangesAsync();
            return (person.Id, item.Id);
        });
        var key = await PeopleSeed.KeyAsync(archive, personId);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/people/{key}")).StatusCode);
        Assert.DoesNotContain(words.Name, await PeopleSeed.TextAsync(visitor, "/people"));

        clock.Now = clock.Now.AddDays(11);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/people/{key}")).StatusCode);
        Assert.Contains(words.Name, await PeopleSeed.TextAsync(visitor, "/people"));

        await archive.WithDbAsync(async db =>
        {
            db.People.Single(p => p.Id == personId).SetDisclosure(PersonDisclosure.Hidden, DateTime.UtcNow);
            return await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/people/{key}")).StatusCode);
        Assert.DoesNotContain(words.Name, await PeopleSeed.TextAsync(visitor, $"/items/{itemId}"));
        Assert.DoesNotContain(itemId.ToString(), ItemIdsIn(await PeopleSeed.TextAsync(visitor, $"/search?q={Uri.EscapeDataString(words.Name)}")));
    }

    [Fact]
    public async Task A_contributor_with_the_name_of_a_hidden_person_is_not_a_link_to_them_and_does_not_make_the_person_public()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        using var visitor = archive.CreateClient();
        var words = PersonWords.New("c");
        var (itemId, key) = await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = "Box", CreatedAt = now };
            var item = PeopleSeed.NewItem(collection, $"Letter {TestData.UniqueWord("c")}", now, true);
            item.Contribution = new Contribution
            {
                ContributorName = words.Name, ContributorEmail = "c@example.org", ShowName = false, Rights = RightsStatement.PermissionFromRightsHolder,
                RightsHolder = "Someone", ConsentText = "I agree.", SubmittedAt = now,
            };
            var person = PeopleSeed.NewPerson(words, PersonDisclosure.Hidden, now);
            db.AddRange(collection, item, person, PeopleSeed.Link(item, person, now));
            await db.SaveChangesAsync();
            return (item.Id, person.PublicId);
        });

        var page = await PeopleSeed.TextAsync(visitor, $"/items/{itemId}");

        Assert.DoesNotContain(words.Name, page);
        Assert.DoesNotContain(key, page);
        Assert.DoesNotContain(words.Name, await PeopleSeed.TextAsync(visitor, "/people"));
    }

    [Fact]
    public async Task The_people_index_and_the_link_to_it_follow_what_is_public()
    {
        await using var archive = await PeopleSeed.NewArchiveAsync(app);
        using var visitor = archive.CreateClient();
        Assert.DoesNotContain("All people", await PeopleSeed.TextAsync(visitor, "/"));
        var s = await SeedAsync(archive);

        var home = await PeopleSeed.TextAsync(visitor, "/");
        var people = await PeopleSeed.TextAsync(visitor, "/people");

        Assert.Contains("href=\"/people\">All people</a>", home);
        Assert.Contains($"href=\"/people/{s.DeceasedKey}\"", people);
        Assert.Contains($"href=\"/people/{s.LivingKey}\"", people);
        Assert.Contains("aria-label=\"1 item\">(1)</span>", people);
        Assert.DoesNotContain(s.Deceased.Marked, people);
    }
}
