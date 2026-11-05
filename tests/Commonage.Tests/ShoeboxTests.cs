using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Pages.Admin.Items;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class ShoeboxTests(CommonageAppFactory app)
{
    private static readonly byte[] Photo = File.ReadAllBytes(Path.Combine("Fixtures", "photo-with-gps.jpg"));

    /// <summary>A picture no other test has sent: the same bytes with something else after the end of the image.</summary>
    private static byte[] NewPhoto() => [.. Photo, .. Guid.NewGuid().ToByteArray()];

    private async Task<HttpClient> CuratorAsync()
    {
        var curator = app.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);
        return curator;
    }

    private async Task<T> InDatabaseAsync<T>(Func<CommonageDbContext, Task<T>> read)
    {
        using var scope = app.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<CommonageDbContext>());
    }

    private async Task<int> NewCollectionAsync(string title = "Shoebox")
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
        var collection = new Collection { Title = $"{title} {Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
        db.Collections.Add(collection);
        await db.SaveChangesAsync();
        return collection.Id;
    }

    private async Task<int> NewItemAsync(string title, ItemStatus status = ItemStatus.Draft, int? collectionId = null)
    {
        var collection = collectionId ?? await NewCollectionAsync();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
        var now = DateTime.UtcNow;
        var item = new ArchiveItem { Title = title, Status = status, CollectionId = collection, CreatedAt = now, UpdatedAt = now };
        db.ArchiveItems.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    /// <summary>What uploads.js does: one file in a request that asks for an answer it can read.</summary>
    private static async Task<(HttpStatusCode Status, JsonElement Reply)> SendOneAsync(
        HttpClient client, string pageUrl, string postUrl, string fileName, byte[] content, params (string Key, string Value)[] fields)
    {
        var page = await client.GetStringAsync(pageUrl);
        using var form = new MultipartFormDataContent { { new StringContent(Browser.Token(page)), "__RequestVerificationToken" } };
        foreach (var (key, value) in fields)
            form.Add(new StringContent(value), key);
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "files", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, postUrl) { Content = form };
        request.Headers.Accept.ParseAdd("application/json");
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, body.StartsWith('{') ? JsonDocument.Parse(body).RootElement.Clone() : default);
    }

    private Task<(HttpStatusCode Status, JsonElement Reply)> AddManyOneAsync(HttpClient client, int collection, byte[] content, params (string Key, string Value)[] more) =>
        SendOneAsync(client, "/Admin/Items/AddMany", "/Admin/Items/AddMany", "scan-0001.jpg", content,
            [("Input.CollectionId", collection.ToString()), .. more]);

    private static async Task<HttpResponseMessage> BulkAsync(HttpClient client, string handler, params (string Key, string Value)[] fields)
    {
        var page = await client.GetStringAsync("/Admin");
        var values = new List<KeyValuePair<string, string>> { new("__RequestVerificationToken", Browser.Token(page)) };
        values.AddRange(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        var url = handler.Length == 0 ? "/Admin/Items/Bulk" : $"/Admin/Items/Bulk?handler={handler}";
        return await client.PostAsync(url, new FormUrlEncodedContent(values));
    }

    private async Task<ArchiveItem> ItemAsync(int id) =>
        await InDatabaseAsync(db => db.ArchiveItems.Include(i => i.Tags).Include(i => i.MediaFiles).FirstAsync(i => i.Id == id));

    [Fact]
    public async Task Each_photo_becomes_a_draft_without_a_stored_title_and_with_the_shared_values()
    {
        using var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();

        var (status, reply) = await AddManyOneAsync(curator, collection, NewPhoto(),
            ("Input.Date", "circa 1950"), ("Input.Tags", "Mill Street, harvest"), ("Input.RightsUri", RightsOptions.NotEvaluated));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("added", reply.GetProperty("outcome").GetString());
        var item = await ItemAsync(reply.GetProperty("itemId").GetInt32());
        Assert.Equal("", item.Title);
        Assert.Equal(ItemStatus.Draft, item.Status);
        Assert.Equal(collection, item.CollectionId);
        Assert.Equal("circa 1950", item.DateText);
        Assert.Equal(RightsOptions.NotEvaluated, item.RightsUri);
        Assert.Equal(["harvest", "Mill Street"], item.Tags.Select(t => t.Name).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Single(item.MediaFiles);
        var entries = await InDatabaseAsync(db => db.AuditEntries.Where(e => e.ArchiveItemId == item.Id).Select(e => e.Action).ToListAsync());
        Assert.Contains(AuditAction.ItemCreated, entries);
    }

    [Fact]
    public async Task Without_javascript_all_files_go_in_one_request_and_become_drafts()
    {
        using var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();
        var before = await InDatabaseAsync(db => db.ArchiveItems.CountAsync(i => i.CollectionId == collection));
        var page = await curator.GetStringAsync("/Admin/Items/AddMany");

        using var form = new MultipartFormDataContent
        {
            { new StringContent(Browser.Token(page)), "__RequestVerificationToken" },
            { new StringContent(collection.ToString()), "Input.CollectionId" },
            { new ByteArrayContent(NewPhoto()), "files", "one.jpg" },
            { new ByteArrayContent(NewPhoto()), "files", "two.jpg" },
            { new ByteArrayContent("not a picture"u8.ToArray()), "files", "three.txt" },
        };
        var response = await curator.PostAsync("/Admin/Items/AddMany", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("needs=Title", response.RequestMessage!.RequestUri!.Query);
        Assert.Equal(before + 2, await InDatabaseAsync(db => db.ArchiveItems.CountAsync(i => i.CollectionId == collection)));
    }

    [Fact]
    public async Task Shared_values_are_checked_before_any_draft_exists()
    {
        using var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();

        var (status, reply) = await AddManyOneAsync(curator, collection, NewPhoto(), ("Input.Date", "someday"));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotEmpty(reply.GetProperty("errors").EnumerateArray());
        Assert.Equal(0, await InDatabaseAsync(db => db.ArchiveItems.CountAsync(i => i.CollectionId == collection)));
    }

    [Fact]
    public async Task A_file_that_is_refused_says_why_and_leaves_no_draft()
    {
        using var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();

        var (status, reply) = await AddManyOneAsync(curator, collection, "not a picture"u8.ToArray());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("refused", reply.GetProperty("outcome").GetString());
        Assert.Contains("scan-0001.jpg", reply.GetProperty("message").GetString());
        Assert.Equal(0, await InDatabaseAsync(db => db.ArchiveItems.CountAsync(i => i.CollectionId == collection)));
    }

    [Fact]
    public async Task A_file_sent_again_is_recognised_so_starting_over_is_safe()
    {
        using var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();
        var scan = NewPhoto();
        var (_, first) = await AddManyOneAsync(curator, collection, scan);

        var (_, second) = await AddManyOneAsync(curator, collection, scan);

        Assert.Equal("identical", second.GetProperty("outcome").GetString());
        Assert.Equal(first.GetProperty("itemId").GetInt32(), second.GetProperty("itemId").GetInt32());
        Assert.Equal(1, await InDatabaseAsync(db => db.ArchiveItems.CountAsync(i => i.CollectionId == collection)));

        var (_, third) = await AddManyOneAsync(curator, collection, scan, ("addIdentical", "true"));

        Assert.Equal("added", third.GetProperty("outcome").GetString());
        Assert.Equal(2, await InDatabaseAsync(db => db.ArchiveItems.CountAsync(i => i.CollectionId == collection)));
    }

    [Fact]
    public async Task Sending_photos_one_at_a_time_to_an_item_leaves_one_line_in_its_history()
    {
        using var curator = await CuratorAsync();
        var id = await NewItemAsync("An album");
        var url = $"/Admin/Items/Edit/{id}";

        var (_, first) = await SendOneAsync(curator, url, $"{url}?handler=Upload", "one.jpg", NewPhoto());
        var (_, second) = await SendOneAsync(curator, url, $"{url}?handler=Upload", "two.jpg", NewPhoto());

        Assert.Equal("added", first.GetProperty("outcome").GetString());
        Assert.Equal("added", second.GetProperty("outcome").GetString());
        Assert.Equal(2, (await ItemAsync(id)).MediaFiles.Count);
        Assert.Equal(1, await InDatabaseAsync(db => db.AuditEntries.CountAsync(e => e.ArchiveItemId == id && e.Action == AuditAction.PhotosAdded)));

        var card = await curator.GetStringAsync(second.GetProperty("cardUrl").GetString()!);
        Assert.Contains("two.jpg", card);
        Assert.Contains("Input.AltTexts", card);
        Assert.DoesNotContain("<html", card);
    }

    [Fact]
    public async Task Photos_can_be_sent_with_a_new_item_and_a_bad_form_keeps_nothing()
    {
        using var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();

        var refused = await Browser.SendFormWithFilesAsync(curator, "/Admin/Items/Edit",
            new() { ["Input.Title"] = "With a bad date", ["Input.CollectionId"] = collection.ToString(), ["Input.Date"] = "someday" },
            ("one.jpg", NewPhoto()));
        Assert.Contains("Please choose the photos again.", await refused.Content.ReadAsStringAsync());
        Assert.Equal(0, await InDatabaseAsync(db => db.ArchiveItems.CountAsync(i => i.CollectionId == collection)));

        var refusedFile = await Browser.SendFormWithFilesAsync(curator, "/Admin/Items/Edit",
            new() { ["Input.Title"] = "With a bad file", ["Input.CollectionId"] = collection.ToString() },
            ("notes.txt", "not a picture"u8.ToArray()));
        Assert.Contains("notes.txt", await refusedFile.Content.ReadAsStringAsync());
        Assert.Equal(0, await InDatabaseAsync(db => db.ArchiveItems.CountAsync(i => i.CollectionId == collection)));

        var saved = await Browser.SendFormWithFilesAsync(curator, "/Admin/Items/Edit",
            new() { ["Input.Title"] = "With photos", ["Input.CollectionId"] = collection.ToString() },
            ("one.jpg", NewPhoto()), ("two.jpg", NewPhoto()));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var item = await InDatabaseAsync(db => db.ArchiveItems.Include(i => i.MediaFiles).FirstAsync(i => i.CollectionId == collection));
        Assert.Equal(2, item.MediaFiles.Count);
        Assert.Equal(ItemStatus.Draft, item.Status);
    }

    [Fact]
    public async Task On_a_new_item_saving_as_a_draft_is_the_main_button()
    {
        using var curator = await CuratorAsync();
        await NewCollectionAsync();

        var page = await curator.GetStringAsync("/Admin/Items/Edit");

        Assert.Matches("""class="btn btn-primary">Save as draft""", page);
        Assert.Matches("""value="publish" class="btn btn-outline-primary">""", page);
        Assert.Contains("""enctype="multipart/form-data""", page);
        Assert.DoesNotContain("Save the item first, then add photos", page);
    }

    [Fact]
    public async Task An_item_without_a_real_title_cannot_be_published_in_any_way()
    {
        using var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();
        var id = await NewItemAsync("", collectionId: collection);

        // Not by a form with an empty title,
        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{id}", new Dictionary<string, string>
        {
            ["Input.Title"] = "   ",
            ["Input.CollectionId"] = collection.ToString(),
            ["command"] = "publish",
        });
        Assert.Equal(ItemStatus.Draft, (await ItemAsync(id)).Status);

        // not many at once,
        await BulkAsync(curator, "Apply", ("Action", nameof(BulkAction.Publish)), ("Ids", id.ToString()));
        Assert.Equal(ItemStatus.Draft, (await ItemAsync(id)).Status);

        // and the domain would not have it either.
        Assert.False(new ArchiveItem { Title = "", Status = ItemStatus.Draft }.CanBePublished);
        Assert.Throws<InvalidOperationException>(() => new ArchiveItem { Title = "" }.Publish(DateTime.UtcNow));

        // Once it has a title, it goes like any other, from the same form.
        await Browser.PostFormAsync(curator, $"/Admin/Items/Edit/{id}", new Dictionary<string, string>
        {
            ["Input.Title"] = "The harvest of 1950",
            ["Input.CollectionId"] = collection.ToString(),
            ["command"] = "publish",
        });
        var item = await ItemAsync(id);
        Assert.Equal("The harvest of 1950", item.Title);
        Assert.Equal(ItemStatus.Published, item.Status);
    }

    [Fact]
    public async Task Untitled_drafts_say_so_in_the_language_of_the_page_and_can_be_found()
    {
        using var curator = await CuratorAsync();
        var id = await NewItemAsync("");

        var list = await curator.GetStringAsync("/Admin?needs=Title&status=Draft");
        var editor = await curator.GetStringAsync($"/Admin/Items/Edit/{id}");

        Assert.Contains($"""/Admin/Items/Edit/{id}""", list);
        Assert.Contains("Untitled", list);
        Assert.Contains("has no title yet", editor);
        Assert.DoesNotContain("Untitled", await InDatabaseAsync(db => db.ArchiveItems.Where(i => i.Id == id).Select(i => i.Title).FirstAsync()));
    }

    [Fact]
    public async Task Publishing_many_shows_the_count_first_and_changes_nothing_until_confirmed()
    {
        using var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();
        var ready = await NewItemAsync("Ready", collectionId: collection);
        var alsoReady = await NewItemAsync("Also ready", collectionId: collection);
        var untitled = await NewItemAsync("", collectionId: collection);
        var submitted = await NewItemAsync("A contribution", ItemStatus.Submitted, collection);

        var review = await BulkAsync(curator, "",
            ("action", nameof(BulkAction.Publish)), ("scope", "selected"),
            ("selected", ready.ToString()), ("selected", alsoReady.ToString()), ("selected", untitled.ToString()), ("selected", submitted.ToString()));
        var reviewPage = await review.Content.ReadAsStringAsync();

        Assert.Contains("Items chosen: 4", reviewPage);
        Assert.Contains("These become public at once: 2.", reviewPage);
        Assert.Contains("no title yet: 1.", reviewPage);
        Assert.Contains("not drafts: 1.", reviewPage);
        Assert.Equal(ItemStatus.Draft, (await ItemAsync(ready)).Status);

        var done = await BulkAsync(curator, "Apply",
            ("Action", nameof(BulkAction.Publish)), ("Ids", $"{ready},{alsoReady},{untitled},{submitted}"));

        Assert.Contains("Items changed: 2. Left as they were: 2.", await done.Content.ReadAsStringAsync());
        Assert.Equal(ItemStatus.Published, (await ItemAsync(ready)).Status);
        Assert.Equal(ItemStatus.Published, (await ItemAsync(alsoReady)).Status);
        Assert.Equal(ItemStatus.Draft, (await ItemAsync(untitled)).Status);
        Assert.Equal(ItemStatus.Submitted, (await ItemAsync(submitted)).Status);
        Assert.Equal(1, await InDatabaseAsync(db => db.AuditEntries.CountAsync(e => e.ArchiveItemId == ready && e.Action == AuditAction.ItemPublished)));
        Assert.Equal(0, await InDatabaseAsync(db => db.AuditEntries.CountAsync(e => e.ArchiveItemId == untitled && e.Action == AuditAction.ItemPublished)));
    }

    [Fact]
    public async Task The_whole_filter_can_be_chosen_and_what_was_confirmed_is_what_changes()
    {
        using var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();
        var first = await NewItemAsync("First", collectionId: collection);
        var second = await NewItemAsync("Second", collectionId: collection);
        var elsewhere = await NewItemAsync("In another collection");

        var review = await BulkAsync(curator, "",
            ("action", nameof(BulkAction.AddTags)), ("scope", "all"), ("back", $"/Admin?collection={collection}"));
        var page = await review.Content.ReadAsStringAsync();
        Assert.Contains("Items chosen: 2", page);
        var ids = System.Text.RegularExpressions.Regex.Match(page, @"name=""Ids"" value=""([\d,]*)""");
        Assert.True(ids.Success);

        // One more item arrives in the list after the count was shown: it was not agreed to.
        var late = await NewItemAsync("Arrived later", collectionId: collection);
        await BulkAsync(curator, "Apply",
            ("Action", nameof(BulkAction.AddTags)), ("Ids", ids.Groups[1].Value), ("Tags", "Summer fair"), ("Back", $"/Admin?collection={collection}"));

        Assert.Contains("Summer fair", (await ItemAsync(first)).TagNames);
        Assert.Contains("Summer fair", (await ItemAsync(second)).TagNames);
        Assert.Null((await ItemAsync(late)).TagNames);
        Assert.Null((await ItemAsync(elsewhere)).TagNames);
    }

    [Fact]
    public async Task Date_collection_and_rights_are_set_with_one_history_line_per_item_that_changed()
    {
        using var curator = await CuratorAsync();
        var target = await NewCollectionAsync("Target");
        var one = await NewItemAsync("One");
        var two = await NewItemAsync("Two");
        var ids = $"{one},{two}";

        await BulkAsync(curator, "Apply", ("Action", nameof(BulkAction.SetDate)), ("Ids", ids), ("Date", "1950s"));
        await BulkAsync(curator, "Apply", ("Action", nameof(BulkAction.MoveToCollection)), ("Ids", ids), ("CollectionId", target.ToString()));
        await BulkAsync(curator, "Apply", ("Action", nameof(BulkAction.SetRights)), ("Ids", ids), ("RightsUri", RightsOptions.InCopyright));

        var item = await ItemAsync(two);
        Assert.Equal("1950s", item.DateText);
        Assert.Equal(new DateOnly(1950, 1, 1), item.DateFrom);
        Assert.Equal(target, item.CollectionId);
        Assert.Equal(RightsOptions.InCopyright, item.RightsUri);
        Assert.Equal(3, await InDatabaseAsync(db => db.AuditEntries.CountAsync(e => e.ArchiveItemId == two && e.Action == AuditAction.ItemChanged)));

        // Setting what is already there changes nothing, so it adds nothing to the history either.
        await BulkAsync(curator, "Apply", ("Action", nameof(BulkAction.SetDate)), ("Ids", ids), ("Date", "1950s"));
        Assert.Equal(3, await InDatabaseAsync(db => db.AuditEntries.CountAsync(e => e.ArchiveItemId == two && e.Action == AuditAction.ItemChanged)));
    }

    [Fact]
    public async Task Bad_values_are_refused_with_the_page_still_showing_the_choice()
    {
        using var curator = await CuratorAsync();
        var id = await NewItemAsync("One");

        var date = await BulkAsync(curator, "Apply", ("Action", nameof(BulkAction.SetDate)), ("Ids", id.ToString()), ("Date", "someday"));
        var tag = await BulkAsync(curator, "Apply", ("Action", nameof(BulkAction.AddTags)), ("Ids", id.ToString()), ("Tags", new string('x', Tag.MaxLength + 1)));
        var rights = await BulkAsync(curator, "Apply", ("Action", nameof(BulkAction.SetRights)), ("Ids", id.ToString()), ("RightsUri", "https://example.org/mine"));
        var collection = await BulkAsync(curator, "Apply", ("Action", nameof(BulkAction.MoveToCollection)), ("Ids", id.ToString()), ("CollectionId", "999999"));

        Assert.Contains("could not be understood", await date.Content.ReadAsStringAsync());
        Assert.Contains("at most", await tag.Content.ReadAsStringAsync());
        Assert.Contains("one of the statements", await rights.Content.ReadAsStringAsync());
        var page = await collection.Content.ReadAsStringAsync();
        Assert.Contains("Please choose a collection", page);
        Assert.Contains("Items chosen: 1", page);
        var item = await ItemAsync(id);
        Assert.Null(item.DateText);
        Assert.Null(item.RightsUri);
    }

    [Fact]
    public async Task Choosing_nothing_goes_back_to_the_list_with_a_message()
    {
        using var curator = await CuratorAsync();

        var response = await BulkAsync(curator, "", ("action", nameof(BulkAction.Publish)), ("scope", "selected"));

        Assert.Contains("Please tick at least one item.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_way_back_can_only_lead_to_the_list()
    {
        using var noRedirects = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await Browser.SendFormAsync(noRedirects, "/Account/Login", new() { ["Input.Email"] = CommonageAppFactory.AdminEmail, ["Input.Password"] = CommonageAppFactory.AdminPassword });
        var id = await NewItemAsync("One");

        var response = await BulkAsync(noRedirects, "Apply",
            ("Action", nameof(BulkAction.SetDate)), ("Ids", id.ToString()), ("Date", "1950"), ("Back", "https://example.org/elsewhere"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Admin", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Visitors_and_other_people_cannot_reach_any_of_this()
    {
        using var visitor = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var url in new[] { "/Admin/Items/AddMany", "/Admin/Items/Bulk", "/admin/media/status?ids=1" })
        {
            var response = await visitor.GetAsync(url);
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Redirect, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        }
    }

    [Fact]
    public async Task A_contribution_with_a_file_the_archive_has_gets_a_badge_only_curators_see()
    {
        using var curator = await CuratorAsync();
        var scan = NewPhoto();
        var existing = await NewItemAsync("Already here");
        await SendOneAsync(curator, $"/Admin/Items/Edit/{existing}", $"/Admin/Items/Edit/{existing}?handler=Upload", "scan.jpg", scan);

        var collection = await NewCollectionAsync();
        var token = ContributionLink.NewToken();
        await InDatabaseAsync(async db =>
        {
            db.Add(new ContributionLink { Title = "Share your photos", CollectionId = collection, Token = token, IsActive = true, CreatedAt = DateTime.UtcNow });
            return await db.SaveChangesAsync();
        });
        using var contributor = app.CreateClient();
        var title = $"A duplicate from a neighbour {Guid.NewGuid():N}";
        var sent = await Browser.SendFormWithFilesAsync(contributor, $"/contribute/{token}", new()
        {
            ["Input.Title"] = title,
            ["Input.Name"] = "Ada Contributor",
            ["Input.ShowName"] = "true",
            ["Input.Rights"] = nameof(RightsStatement.CreatedByContributor),
            ["Input.Consent"] = "true",
        }, ("same.jpg", scan));
        var answer = await sent.Content.ReadAsStringAsync();
        Assert.Contains("Thank you", answer);

        // Nothing the contributor sees gives away what the archive already holds.
        Assert.DoesNotContain("already", answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Already here", answer);

        var id = await InDatabaseAsync(db => db.ArchiveItems.Where(i => i.Title == title).Select(i => i.Id).FirstAsync());
        var list = await curator.GetStringAsync("/Admin?status=Submitted");
        Assert.Contains("The archive already has this file", list);
        var review = await curator.GetStringAsync($"/Admin/Items/Edit/{id}");
        Assert.Contains("The archive already has this file", review);
        Assert.Contains("Already here", review);
    }

    [Fact]
    public async Task Status_says_which_previews_are_done()
    {
        using var curator = await CuratorAsync();
        var id = await NewItemAsync("Album");
        var (_, reply) = await SendOneAsync(curator, $"/Admin/Items/Edit/{id}", $"/Admin/Items/Edit/{id}?handler=Upload", "one.jpg", NewPhoto());
        var mediaFileId = reply.GetProperty("mediaFileId").GetInt32();

        var ready = false;
        for (var attempt = 0; attempt < 100 && !ready; attempt++)
        {
            var status = JsonDocument.Parse(await curator.GetStringAsync($"/admin/media/status?ids={mediaFileId},nonsense,0"));
            ready = status.RootElement.GetProperty("ready").EnumerateArray().Any(e => e.GetInt32() == mediaFileId);
            if (!ready)
                await Task.Delay(100);
        }

        Assert.True(ready);
    }
}
