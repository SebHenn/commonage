using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Commonage.Web.Domain;
using Commonage.Web.Operations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

/// <summary>The JSON of a public item and its Dublin Core tags: what the page says, for programs, and not a word more.</summary>
[Collection(nameof(AppCollection))]
public class PublicItemJsonTests(CommonageAppFactory app)
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record Seeded(string Word, int ItemId, string StorageId);

    private async Task<Seeded> SeedAsync(
        string kind, Action<ArchiveItem, DateTime>? state = null, bool consent = true, MediaLevel level = MediaLevel.Everything, bool tagged = false)
    {
        var word = TestData.UniqueWord(kind);
        var storage = Guid.NewGuid();
        return await app.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = $"Box {word}", CreatedAt = now };
            var item = new ArchiveItem
            {
                Title = $"Title {word}", Description = $"Words {word}", Collection = collection, CreatedAt = now, UpdatedAt = now,
                Credit = $"Credit {word}", MediaLevel = level, RightsUri = RightsOptions.CcBy,
                RightsHolder = $"Holder {word}", ReferenceNumber = $"Ref {word}", OriginalLocation = $"Attic {word}",
                ReceivedFrom = $"Giver {word}", ReceivedOn = $"Received {word}", InternalNotes = $"Notes {word}", NeedsTranscription = true,
                Contribution = new Contribution
                {
                    ContributorName = $"Name {word}", ContributorEmail = $"{word}@example.org", ConsentText = $"Consent {word}",
                    ConsentCoversSharing = consent, SubmittedAt = now, ReferenceCode = $"RC{word}"[..9],
                },
            };
            item.SetDateRange($"around {word}", new DateOnly(1948, 1, 1), new DateOnly(1952, 12, 31));
            // Tags are what the search page ranks as popular, so only the tests that read them add one.
            if (tagged)
                item.SetTags([new Tag { Name = $"Zone {word}", NormalizedName = $"zone {word}", Kind = TagKind.Place }]);
            item.MediaFiles.Add(new MediaFile
            {
                ArchiveItem = item, OriginalFileName = $"original-{word}.jpg", ContentType = "image/jpeg", Sha256 = new string('a', 64),
                SizeBytes = 123456789, StorageId = storage, Status = ProcessingStatus.Ready, AltText = $"Alt {word}", Caption = $"Caption {word}",
                ExtractedText = $"Pages {word}",
            });
            item.Publish(now);
            state?.Invoke(item, now);
            db.AddRange(collection, item);
            await db.SaveChangesAsync();
            return new Seeded(word, item.Id, storage.ToString("N"));
        });
    }

    private async Task<(HttpResponseMessage Response, string Body)> GetAsync(HttpClient client, string address)
    {
        var response = await client.GetAsync(address);
        return (response, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_json_says_what_the_page_says_and_none_of_what_is_private()
    {
        var item = await SeedAsync("everything", tagged: true);
        using var visitor = app.CreateClient();

        var (response, body) = await GetAsync(visitor, $"/items/{item.ItemId}.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=600", response.Headers.CacheControl?.ToString());
        Assert.Null(response.Headers.ETag);
        Assert.Null(response.Content.Headers.LastModified);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal($"Title {item.Word}", root.GetProperty("title").GetString());
        Assert.Equal($"Words {item.Word}", root.GetProperty("description").GetString());
        Assert.Equal($"around {item.Word}", root.GetProperty("dateText").GetString());
        Assert.Equal("1948-01-01", root.GetProperty("dateFrom").GetString());
        Assert.Equal("1952-12-31", root.GetProperty("dateTo").GetString());
        Assert.Equal($"Credit {item.Word}", root.GetProperty("credit").GetString());
        Assert.Equal(RightsOptions.CcBy, root.GetProperty("rights").GetString());
        Assert.EndsWith($"/items/{item.ItemId}", root.GetProperty("identifier").GetString());
        Assert.Equal($"Box {item.Word}", root.GetProperty("collection").GetString());
        Assert.Equal($"Zone {item.Word}", Assert.Single(root.GetProperty("subjects").EnumerateArray()).GetString());
        Assert.Equal("Image", root.GetProperty("type").GetString());
        var file = Assert.Single(root.GetProperty("files").EnumerateArray());
        Assert.Matches(@"/media/\d+/preview$", file.GetProperty("url").GetString());
        Assert.Equal($"Alt {item.Word}", file.GetProperty("altText").GetString());

        // Everything a curator or a contributor keeps to themselves, and what names a stored file, is filled in, and none is there.
        foreach (var private_ in new[]
        {
            $"Holder {item.Word}", $"Ref {item.Word}", $"Attic {item.Word}", $"Giver {item.Word}", $"Received {item.Word}",
            $"Notes {item.Word}", $"Name {item.Word}", $"{item.Word}@example.org", $"Consent {item.Word}", $"original-{item.Word}",
            new string('a', 64), "123456789", item.StorageId, $"Pages {item.Word}", "needsTranscription", "sizeBytes", "sha256",
            "originalFileName", "contribution", "next", "previous", "total",
        })
            Assert.DoesNotContain(private_, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_record_only_item_lists_no_files()
    {
        var item = await SeedAsync("recordonly", level: MediaLevel.RecordOnly);
        using var visitor = app.CreateClient();

        var (response, body) = await GetAsync(visitor, $"/items/{item.ItemId}.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Empty(json.RootElement.GetProperty("files").EnumerateArray());
        Assert.False(json.RootElement.TryGetProperty("type", out _));
        Assert.Equal($"Words {item.Word}", json.RootElement.GetProperty("description").GetString());
        Assert.DoesNotContain($"Alt {item.Word}", body);
        Assert.DoesNotContain($"Caption {item.Word}", body);
        Assert.DoesNotContain("/media/", body);
    }

    [Fact]
    public async Task An_item_that_may_not_travel_leaves_out_its_description_and_files_in_the_json_and_the_tags()
    {
        var item = await SeedAsync("notravel", consent: false);
        using var visitor = app.CreateClient();

        var (_, body) = await GetAsync(visitor, $"/items/{item.ItemId}.json");
        var (_, page) = await GetAsync(visitor, $"/items/{item.ItemId}");

        using var json = JsonDocument.Parse(body);
        Assert.Equal($"Title {item.Word}", json.RootElement.GetProperty("title").GetString());
        Assert.False(json.RootElement.TryGetProperty("description", out _));
        Assert.Empty(json.RootElement.GetProperty("files").EnumerateArray());
        Assert.DoesNotContain($"Words {item.Word}", body);
        Assert.Contains("name=\"DC.title\"", page);
        Assert.DoesNotContain("name=\"DC.description\"", page);
        Assert.DoesNotContain("name=\"DC.type\"", page);
    }

    [Fact]
    public async Task Whatever_a_visitor_may_not_see_answers_exactly_as_if_it_did_not_exist()
    {
        var hidden = new List<Seeded>
        {
            await SeedAsync("draft", (i, _) => i.Status = ItemStatus.Draft),
            await SeedAsync("binned", (i, now) => i.MoveToBin(now)),
            await SeedAsync("embargoed", (i, now) => i.SetPublicFrom(DateOnly.FromDateTime(now).AddDays(3), now)),
            await SeedAsync("rejected", (i, _) => i.Status = ItemStatus.Rejected),
            await SeedAsync("withdrawn", (i, _) => i.Status = ItemStatus.Withdrawn),
            await SeedAsync("submitted", (i, _) => i.Status = ItemStatus.Submitted),
        };
        using var visitor = app.CreateClient();
        var (missingResponse, missingBody) = await GetAsync(visitor, "/items/2000000000.json");

        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Contains("no-store", missingResponse.Headers.CacheControl?.ToString());
        foreach (var item in hidden)
        {
            var (response, body) = await GetAsync(visitor, $"/items/{item.ItemId}.json");
            var (pageResponse, _) = await GetAsync(visitor, $"/items/{item.ItemId}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, pageResponse.StatusCode);
            Assert.Equal(WithoutTokens(missingBody), WithoutTokens(body));
            Assert.Equal(Headers(missingResponse), Headers(response));
            Assert.DoesNotContain(item.Word, body);
        }

        // The page of an item that is not there has no alternate link and no tags to give it away either.
        Assert.DoesNotContain("application/json", missingBody);
        Assert.DoesNotContain("DC.", missingBody);
    }

    // Every page carries a form token that is new with each response.
    private static string WithoutTokens(string page) => Regex.Replace(page, "value=\"[^\"]{40,}\"", "value=\"\"");

    private static string Headers(HttpResponseMessage response) => string.Join('\n',
        response.Headers.Concat(response.Content.Headers).Where(h => h.Key is not ("Date" or "Set-Cookie")).OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => $"{h.Key}: {string.Join(',', h.Value)}"));

    [Fact]
    public async Task An_embargoed_item_is_not_there_today_and_is_there_tomorrow()
    {
        var item = await SeedAsync("tomorrow", (i, now) => i.SetPublicFrom(new DateOnly(2031, 5, 10), now));
        async Task<HttpStatusCode> StatusAtAsync(DateTimeOffset utc)
        {
            await using var archive = app.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(new FixedClock(utc))));
            using var visitor = archive.CreateClient();
            return (await visitor.GetAsync($"/items/{item.ItemId}.json")).StatusCode;
        }

        Assert.Equal(HttpStatusCode.NotFound, await StatusAtAsync(new DateTimeOffset(2031, 5, 9, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(HttpStatusCode.OK, await StatusAtAsync(new DateTimeOffset(2031, 5, 10, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task The_page_names_the_json_and_carries_the_dublin_core_tags_only_when_it_is_visible()
    {
        var shown = await SeedAsync("tags", tagged: true);
        var draft = await SeedAsync("tagsdraft", (i, _) => i.Status = ItemStatus.Draft);
        using var visitor = app.CreateClient();

        var (_, page) = await GetAsync(visitor, $"/items/{shown.ItemId}");
        var (_, hiddenPage) = await GetAsync(visitor, $"/items/{draft.ItemId}");

        Assert.Matches($"""<link rel="alternate" type="application/json" href="https?://[^"]+/items/{shown.ItemId}.json" />""", page);
        Assert.Contains($"""<meta name="DC.title" content="Title {shown.Word}" />""", page);
        Assert.Contains($"""<meta name="DC.description" content="Words {shown.Word}" />""", page);
        Assert.Contains($"""<meta name="DC.date" content="around {shown.Word}" />""", page);
        Assert.Contains($"""<meta name="DC.creator" content="Credit {shown.Word}" />""", page);
        Assert.Contains($"""<meta name="DC.rights" content="{RightsOptions.CcBy}" />""", page);
        Assert.Contains($"""<meta name="DC.subject" content="Zone {shown.Word}" />""", page);
        Assert.Contains("""<meta name="DC.type" content="Image" />""", page);
        Assert.Matches($"""<meta name="DC.identifier" content="https?://[^"]+/items/{shown.ItemId}" />""", page);
        Assert.DoesNotContain("DC.", hiddenPage);
        Assert.DoesNotContain("application/json", hiddenPage);
        Assert.DoesNotContain($"Ref {shown.Word}", page);
    }

    [Fact]
    public async Task With_search_engines_off_the_json_says_noindex_and_the_page_has_no_dublin_core()
    {
        var item = await SeedAsync("noengines");
        using var visitor = app.CreateClient();
        var before = await app.WithDbAsync(db => db.ArchiveSettings.AsNoTracking().FirstOrDefaultAsync());
        await SetAllowAsync(false);
        try
        {
            var (response, _) = await GetAsync(visitor, $"/items/{item.ItemId}.json");
            var (_, page) = await GetAsync(visitor, $"/items/{item.ItemId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
            Assert.DoesNotContain("DC.", page);
            Assert.Matches(new Regex("""rel="alternate" type="application/json"""), page);
        }
        finally
        {
            await SetAllowAsync(before?.AllowSearchEngines ?? true);
        }

        async Task SetAllowAsync(bool allow)
        {
            await app.WithDbAsync(async db =>
            {
                var settings = await db.ArchiveSettings.FirstOrDefaultAsync();
                if (settings is null)
                    db.ArchiveSettings.Add(settings = new ArchiveSettings());
                settings.AllowSearchEngines = allow;
                return await db.SaveChangesAsync();
            });
            app.Services.GetRequiredService<ArchiveProfile>().Forget();
        }
    }

    [Fact]
    public async Task The_sitemap_does_not_list_the_json()
    {
        var item = await SeedAsync("sitemap");
        using var visitor = app.CreateClient();

        var (response, body) = await GetAsync(visitor, "/sitemap.xml");

        if (response.StatusCode == HttpStatusCode.OK)
        {
            Assert.Contains($"/items/{item.ItemId}<", body);
            Assert.DoesNotContain(".json", body);
        }
    }
}
