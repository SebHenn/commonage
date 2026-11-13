using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Commonage.Web.Domain;
using Commonage.Web.Operations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

/// <summary>"Do you know more about this?": visitors write to the curators about a published item, and only curators read it.</summary>
[Collection(nameof(AppCollection))]
public class VisitorNoteTests(CommonageAppFactory app)
{
    private const string Question = "Do you know more about this?";

    /// <summary>An archive of its own with a published item, a draft and an item in the bin.</summary>
    private static async Task<(WebApplicationFactory<Program> Archive, int Published, int Draft, int Binned)> ArchiveAsync(CommonageAppFactory app, bool on)
    {
        var archive = await app.NewArchiveAsync();
        var ids = await archive.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            var collection = new Collection { Title = "Town", CreatedAt = now };
            ArchiveItem Item(string title) => new() { Title = title, Collection = collection, CreatedAt = now, UpdatedAt = now, Status = ItemStatus.Draft };
            var published = Item("Mill Street flood");
            published.Publish(now);
            var draft = Item("Letter, not yet checked");
            var binned = Item("Old harbour");
            binned.Publish(now);
            binned.MoveToBin(now);
            db.ArchiveItems.AddRange(published, draft, binned);
            var settings = await db.ArchiveSettings.FirstOrDefaultAsync();
            if (settings is null)
                db.ArchiveSettings.Add(settings = new ArchiveSettings());
            settings.AcceptVisitorNotes = on;
            await db.SaveChangesAsync();
            return (published.Id, draft.Id, binned.Id);
        });
        archive.Services.GetRequiredService<ArchiveProfile>().Forget();
        return (archive, ids.Item1, ids.Item2, ids.Item3);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient visitor, int itemId, string kind, string message, string? name = null, string? email = null, string? website = null)
    {
        var fields = new Dictionary<string, string> { ["Input.Kind"] = kind, ["Input.Message"] = message };
        if (name is not null)
            fields["Input.Name"] = name;
        if (email is not null)
            fields["Input.Email"] = email;
        if (website is not null)
            fields["Website"] = website;
        return Browser.SendFormWithFilesAsync(visitor, $"/items/{itemId}/tell", fields);
    }

    /// <summary>A form posted to an address without a page to take the token from, with one from the start page.</summary>
    private static async Task<HttpStatusCode> PostWithoutPageAsync(HttpClient visitor, string url)
    {
        var token = Browser.Token(await visitor.GetStringAsync("/"));
        return (await visitor.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }))).StatusCode;
    }

    private static string Decoded(string html) => System.Net.WebUtility.HtmlDecode(html);

    private static async Task<HttpClient> CuratorAsync(WebApplicationFactory<Program> archive)
    {
        var curator = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        return curator;
    }

    [Fact]
    public async Task Visitors_are_not_asked_until_the_archive_turns_it_on()
    {
        var (archive, published, _, _) = await ArchiveAsync(app, on: false);
        await using var _ = archive;
        using var visitor = archive.CreateClient();

        Assert.DoesNotContain(Question, await visitor.GetStringAsync($"/items/{published}"));
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/items/{published}/tell")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, await PostWithoutPageAsync(visitor, $"/items/{published}/tell"));
    }

    [Fact]
    public async Task A_note_reaches_the_curators_and_nothing_of_it_is_public()
    {
        var (archive, published, _, _) = await ArchiveAsync(app, on: true);
        await using var _ = archive;
        using var visitor = archive.CreateClient();

        var item = await visitor.GetStringAsync($"/items/{published}");
        Assert.Contains(Question, item);
        Assert.Contains($"href=\"/items/{published}/tell\"", item);
        var form = await visitor.GetStringAsync($"/items/{published}/tell");
        Assert.Contains("<meta name=\"robots\" content=\"noindex\" />", form);

        var sent = await SendAsync(visitor, published, "Information", "The man on the left is my grandfather, Karl Becker.", "Anna Krause", "anna@example.org");
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Contains("Your message has reached the archive's curators.", Decoded(await sent.Content.ReadAsStringAsync()));

        var note = await archive.WithDbAsync(db => db.VisitorNotes.SingleAsync());
        Assert.Equal((VisitorNoteKind.Information, "Anna Krause", "anna@example.org", published, (DateTime?)null),
            (note.Kind, note.Name, note.Email, note.ArchiveItemId, note.HandledAt));
        // Not on the item's page, and not in the activity log, which is kept longer than personal details should be.
        Assert.DoesNotContain("grandfather", await visitor.GetStringAsync($"/items/{published}"));
        using var curator = await CuratorAsync(archive);
        var log = await curator.GetStringAsync("/Admin/Activity");
        Assert.Contains("A visitor sent a note about the item", log);
        Assert.DoesNotContain("Anna", log);
        Assert.DoesNotContain("anna@example.org", log);

        var notes = await curator.GetStringAsync("/Admin/Notes");
        Assert.Contains("my grandfather, Karl Becker", notes);
        Assert.Contains("anna@example.org", notes);
        Assert.Contains("A visitor sent a note about this item.", await curator.GetStringAsync($"/Admin/Items/Edit/{published}"));
        Assert.Contains("Mill Street flood", await curator.GetStringAsync("/Admin/Contributors?q=anna@example"));
    }

    [Fact]
    public async Task Too_many_notes_from_one_address_are_refused_with_or_without_a_trailing_slash()
    {
        var (archive, published, _, _) = await ArchiveAsync(app, on: true);
        await using var strict = archive.WithWebHostBuilder(b => b.UseSetting("Contributions:MaxSubmissionsPerHour", "2"));
        using var visitor = strict.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var statuses = new List<HttpStatusCode>();
        foreach (var path in new[] { $"/items/{published}/tell", $"/items/{published}/tell/", $"/items/{published}/tell/" })
        {
            var token = Browser.Token(await visitor.GetStringAsync($"/items/{published}/tell"));
            var fields = new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["Input.Kind"] = "Information", ["Input.Message"] = "Hello" };
            statuses.Add((await visitor.PostAsync(path, new FormUrlEncodedContent(fields))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[2]);
    }

    [Fact]
    public async Task Only_published_items_can_be_written_about()
    {
        var (archive, _, draft, binned) = await ArchiveAsync(app, on: true);
        await using var _ = archive;
        using var visitor = archive.CreateClient();

        foreach (var id in new[] { draft, binned, 999999 })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/items/{id}/tell")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, await PostWithoutPageAsync(visitor, $"/items/{id}/tell"));
        }
        Assert.Equal(0, await archive.WithDbAsync(db => db.VisitorNotes.IgnoreQueryFilters().CountAsync()));
    }

    [Fact]
    public async Task Spam_is_thanked_and_dropped_and_mistakes_are_explained()
    {
        var (archive, published, _, _) = await ArchiveAsync(app, on: true);
        await using var _ = archive;
        using var visitor = archive.CreateClient();

        var spam = await SendAsync(visitor, published, "Information", "Cheap watches", website: "https://spam.example");
        Assert.Contains("Your message has reached the archive's curators.", Decoded(await spam.Content.ReadAsStringAsync()));

        var incomplete = await (await SendAsync(visitor, published, "", "")).Content.ReadAsStringAsync();
        Assert.Contains("Please choose what you would like to tell us.", incomplete);
        Assert.Contains("Please write your message.", incomplete);
        var badEmail = await (await SendAsync(visitor, published, "Correction", "The date is wrong.", email: "not an address")).Content.ReadAsStringAsync();
        Assert.Contains("Please enter a valid email address.", badEmail);

        Assert.Equal(0, await archive.WithDbAsync(db => db.VisitorNotes.CountAsync()));
    }

    [Fact]
    public async Task Curators_deal_with_notes_and_remove_what_identifies_the_visitor()
    {
        var (archive, published, _, _) = await ArchiveAsync(app, on: true);
        await using var _ = archive;
        using var visitor = archive.CreateClient();
        await SendAsync(visitor, published, "Information", "It was 1953, not 1950.", "Karl", "karl@example.org");
        await SendAsync(visitor, published, "Removal", "This photo shows me. Please take it down.", "Erna", "erna@example.org");
        var (info, removal) = await archive.WithDbAsync(async db => (
            await db.VisitorNotes.Where(n => n.Kind == VisitorNoteKind.Information).Select(n => n.Id).SingleAsync(),
            await db.VisitorNotes.Where(n => n.Kind == VisitorNoteKind.Removal).Select(n => n.Id).SingleAsync()));
        using var curator = await CuratorAsync(archive);

        // A request to take something down comes first, though it arrived last... and the other way round.
        var open = await curator.GetStringAsync("/Admin/Notes");
        Assert.True(open.IndexOf("This photo shows me", StringComparison.Ordinal) < open.IndexOf("It was 1953", StringComparison.Ordinal));
        Assert.Contains("Open (2)", open);

        await Browser.PostFormAsync(curator, $"/Admin/Notes?handler=Handled&noteId={info}", new(), "/Admin/Notes");
        Assert.DoesNotContain("It was 1953", await curator.GetStringAsync("/Admin/Notes"));
        Assert.Contains("It was 1953", await curator.GetStringAsync("/Admin/Notes?show=Handled"));
        await Browser.PostFormAsync(curator, $"/Admin/Notes?handler=Reopen&noteId={info}", new(), "/Admin/Notes");
        Assert.Contains("It was 1953", await curator.GetStringAsync("/Admin/Notes"));

        await Browser.PostFormAsync(curator, $"/Admin/Notes?handler=RemoveDetails&noteId={removal}", new(), "/Admin/Notes");
        var removed = await archive.WithDbAsync(db => db.VisitorNotes.SingleAsync(n => n.Id == removal));
        Assert.Equal((null, null), (removed.Name, removed.Email));
        Assert.NotNull(removed.PersonalDetailsRemovedAt);
        Assert.Contains("This photo shows me", await curator.GetStringAsync("/Admin/Notes"));
        Assert.DoesNotContain("Mill Street flood", await curator.GetStringAsync("/Admin/Contributors?q=erna"));

        await Browser.PostFormAsync(curator, $"/Admin/Notes?handler=Delete&noteId={info}", new(), "/Admin/Notes");
        Assert.False(await archive.WithDbAsync(db => db.VisitorNotes.AnyAsync(n => n.Id == info)));

        var log = Decoded(await curator.GetStringAsync("/Admin/Activity?what=Contributions"));
        foreach (var done in new[] { "Marked a visitor's note as dealt with", "Opened a visitor's note again", "Removed the name and email address from a visitor's note", "Deleted a visitor's note" })
            Assert.Contains(done, log);
        Assert.DoesNotContain("erna", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Notes_go_with_their_item_to_the_bin_and_come_back_with_it()
    {
        var (archive, published, _, _) = await ArchiveAsync(app, on: true);
        await using var _ = archive;
        using var visitor = archive.CreateClient();
        await SendAsync(visitor, published, "Correction", "The street is misspelt.");
        using var curator = await CuratorAsync(archive);

        await archive.WithDbAsync(async db =>
        {
            (await db.ArchiveItems.SingleAsync(i => i.Id == published)).MoveToBin(DateTime.UtcNow);
            return await db.SaveChangesAsync();
        });
        Assert.DoesNotContain("The street is misspelt.", await curator.GetStringAsync("/Admin/Notes"));

        await archive.WithDbAsync(async db =>
        {
            (await db.ArchiveItems.IgnoreQueryFilters().SingleAsync(i => i.Id == published)).RestoreFromBin(DateTime.UtcNow);
            return await db.SaveChangesAsync();
        });
        Assert.Contains("The street is misspelt.", await curator.GetStringAsync("/Admin/Notes"));
    }

    [Fact]
    public async Task The_export_has_notes_but_a_copy_of_what_is_public_does_not()
    {
        var (archive, published, _, _) = await ArchiveAsync(app, on: true);
        await using var _ = archive;
        using var visitor = archive.CreateClient();
        await SendAsync(visitor, published, "Information", "My grandfather took this photo.", "Anna", "anna@example.org");
        using var curator = await CuratorAsync(archive);

        async Task<JsonElement> ItemIn(Dictionary<string, string> options)
        {
            var response = await Browser.PostFormAsync(curator, "/Admin/Export", options);
            using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
            using var stream = zip.GetEntry("archive.json")!.Open();
            return JsonDocument.Parse(stream).RootElement.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == published).Clone();
        }

        var full = (await ItemIn(new() { ["IncludePersonalData"] = "true" })).GetProperty("notes")[0];
        Assert.Equal(("Information", "My grandfather took this photo.", "anna@example.org"),
            (full.GetProperty("kind").GetString(), full.GetProperty("message").GetString(), full.GetProperty("email").GetString()));
        var withoutPersonalData = (await ItemIn(new())).GetProperty("notes")[0];
        Assert.Equal(JsonValueKind.Null, withoutPersonalData.GetProperty("email").ValueKind);
        Assert.Equal(JsonValueKind.Null, withoutPersonalData.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, withoutPersonalData.GetProperty("message").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await ItemIn(new() { ["PublishedOnly"] = "true" })).GetProperty("notes").ValueKind);
    }
}
