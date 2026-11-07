using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Commonage.Web.Domain;
using Commonage.Web.Operations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

/// <summary>Whether something may be reused, how to cite it, and what goes along when it is shared beyond this website.</summary>
[Collection(nameof(AppCollection))]
public class SharingTests(CommonageAppFactory app)
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";

    private async Task<ArchiveItem> PublishedAsync(string title, Action<ArchiveItem>? change = null, Contribution? contribution = null) =>
        await app.WithDbAsync(async db =>
        {
            var collection = new Collection { Title = $"Collection {Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
            var item = new ArchiveItem { Title = title, Collection = collection, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Contribution = contribution };
            change?.Invoke(item);
            item.Publish(DateTime.UtcNow);
            db.Add(item);
            await db.SaveChangesAsync();
            return item;
        });

    private static Contribution From(bool consentCoversSharing) => new()
    {
        ContributorName = "Private Person",
        ContributorEmail = "private-person@example.org",
        ConsentText = "I agree.",
        ConsentCoversSharing = consentCoversSharing,
        SubmittedAt = DateTime.UtcNow,
    };

    private async Task AddPhotoAsync(int itemId)
    {
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{itemId}", "Upload", "photo.jpg", TestData.Photo);
        for (var i = 0; i < 100 && await app.WithDbAsync(db => db.MediaFiles.AnyAsync(f => f.ArchiveItemId == itemId && f.Status != ProcessingStatus.Ready)); i++)
            await Task.Delay(100);
    }

    private async Task WithSettingsAsync(Action<ArchiveSettings> change, Func<Task> test)
    {
        var before = await app.WithDbAsync(db => db.ArchiveSettings.AsNoTracking().FirstOrDefaultAsync());
        await Change(settings =>
        {
            change(settings);
        });
        try
        {
            await test();
        }
        finally
        {
            await Change(settings =>
            {
                var original = before ?? new ArchiveSettings();
                settings.AllowSearchEngines = original.AllowSearchEngines;
                settings.DefaultRightsUri = original.DefaultRightsUri;
            });
        }

        async Task Change(Action<ArchiveSettings> apply)
        {
            await app.WithDbAsync(async db =>
            {
                var settings = await db.ArchiveSettings.FirstOrDefaultAsync();
                if (settings is null)
                    db.ArchiveSettings.Add(settings = new ArchiveSettings());
                apply(settings);
                return await db.SaveChangesAsync();
            });
            app.Services.GetRequiredService<ArchiveProfile>().Forget();
        }
    }

    private static string? Meta(string page, string property)
    {
        var match = Regex.Match(page, $"""<meta (?:property|name)="{Regex.Escape(property)}" content="([^"]*)" """.TrimEnd());
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }

    private async Task<XElement> FeedAsync()
    {
        using var visitor = app.CreateClient();
        var response = await visitor.GetAsync("/feed.xml");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/atom+xml", response.Content.Headers.ContentType?.MediaType);
        // Parsing is the test: one wrong character and a feed reader gives up on the whole feed.
        return XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!;
    }

    private static XElement? EntryOf(XElement feed, int itemId) =>
        feed.Elements(Atom + "entry").SingleOrDefault(e => e.Element(Atom + "id")!.Value.EndsWith($"/items/{itemId}", StringComparison.Ordinal));

    [Fact]
    public async Task An_item_says_in_plain_language_whether_it_may_be_reused_and_who_to_credit()
    {
        var item = await PublishedAsync("Free to use", i =>
        {
            i.RightsUri = RightsOptions.CcBy;
            i.Credit = "Photo: Anna Krause";
            i.RightsHolder = "Somebody only curators know";
        });
        using var visitor = app.CreateClient();

        var page = await visitor.GetStringAsync($"/items/{item.Id}");

        Assert.Contains("May I reuse this?", page);
        Assert.Contains("Free to use with credit (CC BY)", page);
        Assert.Contains($"href=\"{RightsOptions.CcBy}\" rel=\"license noopener\"", page);
        Assert.Contains("Photo: Anna Krause", page);
        Assert.DoesNotContain("Somebody only curators know", page);
    }

    [Fact]
    public async Task Without_a_statement_of_its_own_an_item_follows_the_archive_or_says_to_ask()
    {
        var item = await PublishedAsync("Undecided");
        using var visitor = app.CreateClient();

        await WithSettingsAsync(settings => settings.DefaultRightsUri = null, async () =>
        {
            var page = await visitor.GetStringAsync($"/items/{item.Id}");
            Assert.Contains("Nothing has been decided yet: please ask the archive", page);
            Assert.DoesNotContain("rel=\"license", page);
        });

        await WithSettingsAsync(settings => settings.DefaultRightsUri = RightsOptions.InCopyright, async () =>
        {
            var page = await visitor.GetStringAsync($"/items/{item.Id}");
            Assert.Contains("In copyright: ask before reusing", page);
            Assert.DoesNotContain("Nothing has been decided yet", page);
        });
    }

    [Fact]
    public async Task A_citation_says_who_what_when_and_where_to_find_it_and_never_who_sent_it()
    {
        var item = await PublishedAsync("Flood in Mill Street", i =>
        {
            i.Credit = "Photo: Anna Krause";
            i.TrySetDate("March 1962");
        }, From(consentCoversSharing: true));
        using var visitor = app.CreateClient();

        var page = await visitor.GetStringAsync($"/items/{item.Id}");

        var citation = WebUtility.HtmlDecode(Regex.Match(page, """<p id="citation"[^>]*>([^<]*)</p>""").Groups[1].Value);
        Assert.StartsWith("Photo: Anna Krause. Flood in Mill Street. March 1962. ", citation);
        Assert.Contains($"/items/{item.Id} (accessed ", citation);
        Assert.DoesNotContain("Private Person", page);
        Assert.Contains($"<link rel=\"canonical\" href=\"http://localhost/items/{item.Id}\"", page);
    }

    /// <summary>Production runs behind Caddy with the setting in compose.production.yaml. Every address a program copies has to be the public one.</summary>
    [Fact]
    public async Task Behind_the_proxy_every_address_says_https()
    {
        var item = await PublishedAsync("Behind the proxy");
        await AddPhotoAsync(item.Id);
        await using var production = app.WithWebHostBuilder(b => b.UseSetting("FORWARDEDHEADERS_ENABLED", "true"));
        using var visitor = production.CreateClient();
        visitor.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var page = await visitor.GetStringAsync($"/items/{item.Id}");
        var feed = await visitor.GetStringAsync("/feed.xml");
        var sitemap = await visitor.GetStringAsync("/sitemap.xml");
        var robots = await visitor.GetStringAsync("/robots.txt");

        Assert.Contains($"<link rel=\"canonical\" href=\"https://localhost/items/{item.Id}\"", page);
        Assert.StartsWith("https://", Meta(page, "og:image"));
        Assert.Contains($"https://localhost/items/{item.Id} (accessed ", WebUtility.HtmlDecode(page));
        Assert.Contains($"https://localhost/items/{item.Id}", feed);
        Assert.DoesNotContain("http://localhost", feed);
        Assert.DoesNotContain("<loc>http://", sitemap);
        Assert.Contains("Sitemap: https://localhost/sitemap.xml", robots);
    }

    [Fact]
    public async Task A_link_shows_the_picture_title_and_the_beginning_of_the_text()
    {
        var longText = string.Join(' ', Enumerable.Repeat("Mill street in the morning light", 20));
        var item = await PublishedAsync("Morning in Mill Street", i => i.Description = longText);
        await AddPhotoAsync(item.Id);
        using var visitor = app.CreateClient();
        var photo = await app.WithDbAsync(db => db.MediaFiles.Where(f => f.ArchiveItemId == item.Id).Select(f => f.Id).SingleAsync());

        var page = await visitor.GetStringAsync($"/items/{item.Id}");

        Assert.Equal("Morning in Mill Street", Meta(page, "og:title"));
        Assert.Equal("article", Meta(page, "og:type"));
        Assert.Equal($"http://localhost/media/{photo}/card", Meta(page, "og:image"));
        Assert.Equal("Morning in Mill Street", Meta(page, "og:image:alt"));
        var description = Meta(page, "og:description")!;
        Assert.True(description.Length <= 201, $"{description.Length} characters.");
        Assert.EndsWith("…", description);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync(new Uri(Meta(page, "og:image")!).AbsolutePath)).StatusCode);
    }

    [Fact]
    public async Task A_contribution_from_before_only_agreed_to_this_website_so_nothing_of_it_travels()
    {
        var earlier = await PublishedAsync("Sent in earlier", i => i.Description = "Words that were agreed for this website", From(consentCoversSharing: false));
        var later = await PublishedAsync("Sent in later", i => i.Description = "Words that may travel", From(consentCoversSharing: true));
        await AddPhotoAsync(earlier.Id);
        await AddPhotoAsync(later.Id);
        using var visitor = app.CreateClient();

        var earlierPage = await visitor.GetStringAsync($"/items/{earlier.Id}");
        var laterPage = await visitor.GetStringAsync($"/items/{later.Id}");
        var feed = await FeedAsync();

        // The page itself is what they agreed to.
        Assert.Contains("Words that were agreed for this website", earlierPage);
        Assert.Contains($"/media/", earlierPage);
        Assert.Null(Meta(earlierPage, "og:description"));
        Assert.Null(Meta(earlierPage, "og:image"));
        Assert.Equal("Sent in earlier", Meta(earlierPage, "og:title"));
        Assert.Null(EntryOf(feed, earlier.Id));

        Assert.Equal("Words that may travel", Meta(laterPage, "og:description"));
        Assert.NotNull(Meta(laterPage, "og:image"));
        Assert.NotNull(EntryOf(feed, later.Id));
    }

    [Fact]
    public async Task The_feed_holds_what_is_public_and_survives_any_text()
    {
        var item = await PublishedAsync("Fish & <chips> \u0001in the \"market\"", i =>
        {
            i.Description = "A line with \u0008 a control character & <b>markup</b>";
            i.RightsUri = RightsOptions.CcBy;
            i.Credit = "Photo: <Anna>";
        }, From(consentCoversSharing: true));
        await AddPhotoAsync(item.Id);
        var hidden = await PublishedAsync("Not public any more");
        await app.WithDbAsync(async db =>
        {
            (await db.ArchiveItems.SingleAsync(i => i.Id == hidden.Id)).Unpublish();
            return await db.SaveChangesAsync();
        });

        var feed = await FeedAsync();

        var entry = EntryOf(feed, item.Id);
        Assert.NotNull(entry);
        Assert.Equal("Fish & <chips> in the \"market\"", entry.Element(Atom + "title")!.Value);
        Assert.Equal("Free to use with credit (CC BY)", entry.Element(Atom + "rights")!.Value);
        var content = entry.Element(Atom + "content")!.Value;
        Assert.Contains("&lt;b&gt;markup&lt;/b&gt;", content);
        Assert.Contains("/media/", content);
        Assert.Contains("Photo: &lt;Anna&gt;", content);
        Assert.DoesNotContain("Private Person", feed.ToString());
        Assert.DoesNotContain("private-person@example.org", feed.ToString());
        Assert.Null(EntryOf(feed, hidden.Id));
        Assert.True(feed.Elements(Atom + "entry").Count() <= 30);
        Assert.NotNull(feed.Element(Atom + "updated"));
        Assert.NotNull(feed.Element(Atom + "author"));
    }

    [Fact]
    public async Task The_sitemap_lists_what_is_public_and_nothing_else()
    {
        var item = await PublishedAsync("In the sitemap");
        var earlier = await PublishedAsync("From a contribution before", contribution: From(consentCoversSharing: false));
        var hidden = await PublishedAsync("Not in the sitemap");
        await app.WithDbAsync(async db =>
        {
            (await db.ArchiveItems.SingleAsync(i => i.Id == hidden.Id)).Unpublish();
            return await db.SaveChangesAsync();
        });
        using var visitor = app.CreateClient();

        var sitemap = XDocument.Parse(await visitor.GetStringAsync("/sitemap.xml")).Root!;
        var addresses = sitemap.Elements(Sitemap + "url").Select(u => u.Element(Sitemap + "loc")!.Value).ToList();

        Assert.Contains($"http://localhost/items/{item.Id}", addresses);
        // A sitemap only says what is on this website already.
        Assert.Contains($"http://localhost/items/{earlier.Id}", addresses);
        Assert.Contains($"http://localhost/collections/{item.CollectionId}", addresses);
        Assert.Contains("http://localhost/", addresses);
        Assert.DoesNotContain($"http://localhost/items/{hidden.Id}", addresses);
        Assert.DoesNotContain($"http://localhost/collections/{hidden.CollectionId}", addresses);
        Assert.Contains("Sitemap: http://localhost/sitemap.xml", await visitor.GetStringAsync("/robots.txt"));
    }

    [Fact]
    public async Task All_of_it_follows_the_switch_for_search_engines()
    {
        var item = await PublishedAsync("Off the record", i => i.Description = "Some text");
        using var visitor = app.CreateClient();

        await WithSettingsAsync(settings => settings.AllowSearchEngines = false, async () =>
        {
            var page = await visitor.GetStringAsync($"/items/{item.Id}");

            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/feed.xml")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/sitemap.xml")).StatusCode);
            Assert.DoesNotContain("Sitemap:", await visitor.GetStringAsync("/robots.txt"));
            Assert.Null(Meta(page, "og:title"));
            Assert.DoesNotContain("application/atom+xml", page);
            Assert.Contains("noindex", page);
            // The page still says what it is, and how to cite it.
            Assert.Contains("rel=\"canonical\"", page);
            Assert.Contains("id=\"citation\"", page);
        });
    }

    [Fact]
    public async Task Pages_for_curators_and_contributors_show_nothing_when_a_link_to_them_is_shared()
    {
        using var visitor = app.CreateClient();
        var token = await app.WithDbAsync(async db =>
        {
            var link = new ContributionLink { Title = "Send it in", Collection = new Collection { Title = $"Collection {Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow }, Token = ContributionLink.NewToken(), IsActive = true, CreatedAt = DateTime.UtcNow };
            db.Add(link);
            await db.SaveChangesAsync();
            return link.Token;
        });

        Assert.Null(Meta(await visitor.GetStringAsync($"/contribute/{token}"), "og:title"));
        Assert.Null(Meta(await visitor.GetStringAsync("/Account/Login"), "og:title"));
    }

    [Theory]
    [InlineData("CreatedByContributor", "AnyUseWithCredit", "AnyUseWithCredit", "reuse it for any purpose")]
    [InlineData("PermissionFromRightsHolder", "NonCommercialWithCredit", "NonCommercialWithCredit", "non-commercial purposes")]
    [InlineData("Unknown", "AnyUseWithCredit", null, null)]
    [InlineData("CreatedByContributor", "", null, null)]
    public async Task A_contributor_can_offer_reuse_but_not_what_they_do_not_know_to_be_theirs(string rights, string offer, string? stored, string? inConsent)
    {
        var token = await app.WithDbAsync(async db =>
        {
            var link = new ContributionLink { Title = "Send it in", Collection = new Collection { Title = $"Collection {Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow }, Token = ContributionLink.NewToken(), IsActive = true, CreatedAt = DateTime.UtcNow };
            db.Add(link);
            await db.SaveChangesAsync();
            return link.Token;
        });
        using var visitor = app.CreateClient();

        await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", new()
        {
            ["Input.Title"] = "With an offer",
            ["Input.Name"] = "Ada Contributor",
            ["Input.Rights"] = rights,
            ["Input.Reuse"] = offer,
            ["Input.Consent"] = "true",
        });

        var contribution = await app.WithDbAsync(db => db.Contributions.Include(c => c.ArchiveItem).SingleAsync(c => c.ContributionLink!.Token == token));
        Assert.Equal(stored, contribution.Reuse?.ToString());
        Assert.True(contribution.ConsentCoversSharing);
        Assert.Contains("also where a link to it is shared", contribution.ConsentText);
        if (inConsent is null)
            Assert.DoesNotContain("I also allow", contribution.ConsentText);
        else
            Assert.Contains(inConsent, contribution.ConsentText);
        // Nothing is decided by it: visitors are told nothing until a curator publishes.
        Assert.Null(contribution.ArchiveItem.RightsUri);
        Assert.Equal(ItemStatus.Submitted, contribution.ArchiveItem.Status);
    }

    [Fact]
    public async Task A_curator_sees_what_was_offered_as_a_suggestion_and_has_to_decide()
    {
        var offered = await app.WithDbAsync(async db =>
        {
            var item = new ArchiveItem
            {
                Title = "Offered freely",
                Collection = new Collection { Title = $"Collection {Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow },
                Status = ItemStatus.Submitted,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Contribution = new Contribution
                {
                    ContributorName = "Ada",
                    Rights = RightsStatement.CreatedByContributor,
                    Reuse = ReuseOffer.AnyUseWithCredit,
                    ConsentText = "I agree.",
                    SubmittedAt = DateTime.UtcNow,
                },
            };
            db.Add(item);
            await db.SaveChangesAsync();
            return item;
        });
        using var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);

        var page = await curator.GetStringAsync($"/Admin/Items/Edit/{offered.Id}");

        Assert.Contains("Yes, for any purpose, with credit", page);
        Assert.Contains("Suggested because the contributor offered this themselves", page);
        Assert.Matches($"""<option value="{Regex.Escape(RightsOptions.CcBy)}" selected="selected">""", page);
    }

    [Theory]
    [InlineData(RightsStatement.CreatedByContributor, null, RightsOptions.InCopyright)]
    [InlineData(RightsStatement.PermissionFromRightsHolder, null, RightsOptions.InCopyright)]
    [InlineData(RightsStatement.Unknown, null, RightsOptions.NotEvaluated)]
    [InlineData(RightsStatement.CreatedByContributor, ReuseOffer.AnyUseWithCredit, RightsOptions.CcBy)]
    [InlineData(RightsStatement.PermissionFromRightsHolder, ReuseOffer.NonCommercialWithCredit, RightsOptions.CcByNc)]
    [InlineData(RightsStatement.Unknown, ReuseOffer.AnyUseWithCredit, RightsOptions.NotEvaluated)]
    public void A_free_licence_is_only_suggested_when_someone_offered_one(RightsStatement answer, ReuseOffer? offer, string expected)
    {
        Assert.Equal(expected, RightsOptions.ProposalFor(answer, offer));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("Short", "Short")]
    [InlineData("Two\n\nlines   here", "Two lines here")]
    public void A_description_for_a_link_is_one_line(string? text, string? expected)
    {
        Assert.Equal(expected, Commonage.Web.Search.LinkPreview.Shorten(text));
    }
}
