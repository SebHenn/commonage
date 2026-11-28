using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Export;
using Commonage.Web.Import;
using Commonage.Web.Media;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Commonage.Tests;

/// <summary>Reading an export of Commonage's own back in: as drafts, without anyone's personal details, and never more than once.</summary>
[Collection(nameof(AppCollection))]
public partial class ExportImportTests(CommonageAppFactory app)
{
    private const string Page = "/Admin/ImportArchive";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static byte[] NewPhoto() => [.. TestData.Photo, .. Guid.NewGuid().ToByteArray()];

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<HttpClient> CuratorAsync(WebApplicationFactory<Program> archive)
    {
        var client = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(client);
        return client;
    }

    private static byte[] Zip(params (string Name, byte[] Content)[] files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(content);
            }
        }

        return stream.ToArray();
    }

    private static byte[] ArchiveJson(object[] items, object[]? collections = null, int version = 8, string format = "commonage-export", object[]? people = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new { format, version, collections = collections ?? [], tags = Array.Empty<object>(), items, people = people ?? [] }, Json);

    private static object Collection(int id, string title) => new { id, title, description = (string?)null };

    private static object File(int id, int item, string name, byte[] content, string? sha = null, string? path = null,
        string? altText = null, string? caption = null, int rotation = 0) => new
    {
        id,
        path = path ?? $"files/{item}/{id}-{name}",
        originalFileName = name,
        sha256 = sha ?? Sha(content),
        position = 0,
        rotation,
        altText,
        caption,
        problem = (string?)null,
    };

    private static object Item(int id, string title, int collectionId = 1, string status = "Published", object? date = null,
        object? contribution = null, object[]? files = null, string? mediaLevel = null, string? publicFrom = null, string? description = null, int[]? people = null) => new
    {
        id,
        collectionId,
        title,
        description,
        status,
        date,
        tags = Array.Empty<string>(),
        people = people ?? [],
        mediaLevel,
        publicFrom,
        contribution,
        files = files ?? [],
    };

    private static async Task<string> DryRunAsync(HttpClient client, byte[] zip)
    {
        var page = await client.GetStringAsync(Page);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Browser.Token(page)), "__RequestVerificationToken" },
            { new ByteArrayContent(zip), "export", "export.zip" },
        };
        var response = await client.PostAsync(Page, form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task ImportAsync(HttpClient client, string dryRunHtml)
    {
        var id = StagingIdPattern().Match(dryRunHtml).Groups[1].Value;
        Assert.NotEmpty(id);
        var response = await client.PostAsync($"{Page}?handler=Import", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Browser.Token(dryRunHtml),
            ["StagingId"] = id,
        }));
        response.EnsureSuccessStatusCode();
    }

    [GeneratedRegex(@"name=""StagingId""[^>]*value=""([^""]+)""")]
    private static partial Regex StagingIdPattern();

    private async Task<string> RunAsync(HttpClient client, byte[] zip)
    {
        var html = await DryRunAsync(client, zip);
        if (StagingIdPattern().IsMatch(html) && html.Contains("handler=Import"))
            await ImportAsync(client, html);
        return html;
    }

    private Task<List<ArchiveItem>> ItemsAsync(string titleContains) =>
        app.WithDbAsync(db => db.ArchiveItems.IgnoreQueryFilters().Include(i => i.MediaFiles).Include(i => i.Collection)
            .Where(i => i.Title.Contains(titleContains)).OrderBy(i => i.Id).ToListAsync());

    private static string Word() => TestData.UniqueWord("Exportword");

    [Fact]
    public async Task An_export_of_this_app_comes_back_as_drafts_without_a_single_personal_detail()
    {
        await using var from = await app.NewArchiveAsync();
        using var source = await CuratorAsync(from);
        var word = Word();
        var contribution = await from.CreateContributionAsync(ItemStatus.Published, name: "Erika Zeppelin", email: "zeppelin@example.org");
        var photo = NewPhoto();
        await Browser.UploadAsync(source, $"/Admin/Items/Edit/{contribution.Id}", "Upload", "mill.jpg", photo);
        await from.WithDbAsync(async db =>
        {
            var item = await db.ArchiveItems.Include(i => i.Contribution).SingleAsync(i => i.Id == contribution.Id);
            item.Title = $"{word} Mill";
            item.Description = "The old mill in spring";
            item.MediaLevel = MediaLevel.RecordOnly;
            item.PublicFrom = new DateOnly(2099, 6, 1);
            item.SetDateRange("Spring 1953", new DateOnly(1953, 3, 1), new DateOnly(1953, 5, 31));
            item.ReceivedFrom = "Aunt Frieda Hohenzollern";
            item.InternalNotes = "Do not tell anyone about the cellar";
            item.Contribution!.ConsentCoversSharing = true;
            item.Contribution.ConsentText = "I, Erika Zeppelin, agree to everything.";
            db.VisitorNotes.Add(new VisitorNote
            {
                ArchiveItemId = item.Id, Message = "A note from Gustav Visitor", Name = "Gustav Visitor", Email = "gustav@example.org", ReceivedAt = DateTime.UtcNow,
            });
            return await db.SaveChangesAsync();
        });
        var originalSha = await from.WithDbAsync(db => db.MediaFiles.Where(f => f.ArchiveItemId == contribution.Id).Select(f => f.Sha256).SingleAsync());
        var response = await Browser.PostFormAsync(source, "/Admin/Export", new() { ["IncludePersonalData"] = "true" });
        var export = await response.Content.ReadAsByteArrayAsync();
        Assert.Contains("zeppelin@example.org", Encoding.UTF8.GetString(ReadEntry(export, "archive.json")));

        await using var into = await app.NewArchiveAsync();
        using var target = await CuratorAsync(into);
        var html = await DryRunAsync(target, export);
        await ImportAsync(target, html);

        var items = await into.WithDbAsync(db => db.ArchiveItems.IgnoreQueryFilters().Include(i => i.MediaFiles).ToListAsync());
        var imported = Assert.Single(items);
        Assert.Equal($"{word} Mill", imported.Title);
        Assert.Equal("The old mill in spring", imported.Description);
        Assert.Equal(ItemStatus.Draft, imported.Status);
        Assert.Null(imported.PublishedAt);
        Assert.Equal(MediaLevel.RecordOnly, imported.MediaLevel);
        Assert.Equal(new DateOnly(2099, 6, 1), imported.PublicFrom);
        Assert.Equal("Spring 1953", imported.DateText);
        Assert.Equal(new DateOnly(1953, 3, 1), imported.DateFrom);
        Assert.Equal(new DateOnly(1953, 5, 31), imported.DateTo);
        Assert.Equal(originalSha, Assert.Single(imported.MediaFiles).Sha256);
        Assert.Null(imported.ReceivedFrom);
        Assert.Null(imported.InternalNotes);
        Assert.Null(imported.Contribution);

        var everything = await into.WithDbAsync(async db =>
            string.Join('\n', await db.Contributions.Select(c => c.ContributorName + c.ContributorEmail + c.ConsentText).ToListAsync())
            + string.Join('\n', await db.VisitorNotes.Select(n => n.Message + n.Name + n.Email).ToListAsync())
            + string.Join('\n', await db.AuditEntries.Select(a => a.UserName + a.Subject + a.Action).ToListAsync())
            + string.Join('\n', await db.ArchiveItems.IgnoreQueryFilters().Select(i => i.ReceivedFrom + i.InternalNotes + i.Description + i.RightsHolder).ToListAsync()));
        var activity = await target.GetStringAsync("/Admin/Activity");
        foreach (var secret in new[] { "Zeppelin", "zeppelin@example.org", "agree to everything", "Gustav", "gustav@example.org", "Hohenzollern", "cellar" })
        {
            Assert.DoesNotContain(secret, everything);
            Assert.DoesNotContain(secret, html);
            Assert.DoesNotContain(secret, activity);
        }

        Assert.Contains("Imported items from a Commonage export", activity);
        var actions = await into.WithDbAsync(db => db.AuditEntries.Select(e => e.Action).ToListAsync());
        Assert.Contains(AuditAction.ArchiveImported, actions);
        Assert.Contains(AuditAction.ItemCreated, actions);
        Assert.Contains(AuditAction.PhotosAdded, actions);

        // The second run finds everything there and offers nothing.
        var again = await DryRunAsync(target, export);
        Assert.DoesNotContain("handler=Import", again);
        Assert.Single(await into.WithDbAsync(db => db.ArchiveItems.IgnoreQueryFilters().ToListAsync()));
    }

    /// <summary>Stories are only counted: the dry run says how many there are, nothing of them is read, and nothing is created for them.</summary>
    [Fact]
    public async Task Stories_in_an_export_are_counted_by_the_dry_run_and_not_imported()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "commonage-export", version = 9, collections = new[] { Collection(1, $"{word} Collection") }, tags = Array.Empty<object>(),
            items = new[] { Item(1, $"{word} Barn") },
            stories = new object[]
            {
                new { id = 1, title = "First", blocks = new object[] { new { kind = "Text", text = "words" }, new { kind = "Item", itemId = 1, caption = "c" } } },
                new { id = 2, title = (string?)null, blocks = (object?)null, extra = new { deeply = new[] { 1, 2, 3 } } },
            },
        }, Json);
        var storiesBefore = await app.WithDbAsync(db => db.Stories.IgnoreQueryFilters().CountAsync());

        var html = await DryRunAsync(curator, Zip(("archive.json", json)));
        await ImportAsync(curator, html);

        Assert.Contains("The export holds 2 stories. Stories are not imported.", html);
        Assert.Single(await ItemsAsync(word));
        Assert.Equal(storiesBefore, await app.WithDbAsync(db => db.Stories.IgnoreQueryFilters().CountAsync()));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task Versions_four_to_the_current_one_are_read(int version)
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        var json = ArchiveJson([Item(1, $"{word} Barn")], [Collection(1, $"{word} Collection")], version);

        await RunAsync(curator, Zip(("archive.json", json)));

        var item = Assert.Single(await ItemsAsync(word));
        Assert.Equal(ItemStatus.Draft, item.Status);
    }

    [Theory]
    [InlineData(3, "format version 3")]
    [InlineData(11, "format version 11")]
    public async Task Other_versions_and_other_files_are_refused_with_a_sentence(int version, string expected)
    {
        using var curator = await CuratorAsync(app);
        var word = Word();

        var html = await DryRunAsync(curator, Zip(("archive.json", ArchiveJson([Item(1, $"{word} X")], null, version))));

        Assert.Contains(expected, html);
        Assert.Empty(await ItemsAsync(word));
        Assert.DoesNotContain("handler=Import", html);

        var other = await DryRunAsync(curator, Zip(("archive.json", ArchiveJson([], null, 8, "something-else"))));
        Assert.Contains("This is not a Commonage export.", other);
        var plain = await DryRunAsync(curator, Zip(("readme.txt", "hi"u8.ToArray())));
        Assert.Contains("has no archive.json", plain);
    }

    [Fact]
    public async Task A_file_with_the_wrong_checksum_is_left_out_and_not_stored()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        var photo = NewPhoto();
        var json = ArchiveJson(
            [Item(1, $"{word} Barn", files: [File(7, 1, "barn.jpg", photo, sha: Sha([1, 2, 3]))])],
            [Collection(1, $"{word} Collection")]);

        var html = await RunAsync(curator, Zip(("archive.json", json), ("files/1/7-barn.jpg", photo)));

        Assert.Contains("does not match its checksum", html);
        var item = Assert.Single(await ItemsAsync(word));
        Assert.Empty(item.MediaFiles);
    }

    [Fact]
    public async Task Names_that_climb_out_of_the_folder_are_never_looked_up_and_never_stored()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        var photo = NewPhoto();
        var json = ArchiveJson(
            [Item(1, $"{word} Barn", files: [File(7, 1, "barn.jpg", photo, path: "files/1/../../evil.jpg")]),
             Item(2, $"{word} Shed", files: [File(8, 2, "shed.jpg", photo, path: "files/2/8-shed.jpg/../../x.jpg")])],
            [Collection(1, $"{word} Collection")]);

        var html = await RunAsync(curator, Zip(("archive.json", json), ("evil.jpg", photo), ("x.jpg", photo)));

        Assert.Contains("not the one an export writes", html);
        Assert.All(await ItemsAsync(word), item => Assert.Empty(item.MediaFiles));
    }

    [Fact]
    public async Task An_entry_that_lies_about_its_size_does_not_get_a_file_stored_or_break_the_page()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        var photo = NewPhoto();
        var json = ArchiveJson(
            [Item(1, $"{word} Barn", files: [File(7, 1, "barn.jpg", photo)])],
            [Collection(1, $"{word} Collection")]);
        var zip = Zip(("archive.json", json), ("files/1/7-barn.jpg", photo));
        LieAboutSize(zip, "files/1/7-barn.jpg", 10);

        await RunAsync(curator, zip);

        Assert.All(await ItemsAsync(word), item => Assert.Empty(item.MediaFiles));
    }

    [Fact]
    public async Task An_archive_json_that_inflates_beyond_the_limit_is_refused_while_it_is_read()
    {
        using var curator = await CuratorAsync(app);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("archive.json", CompressionLevel.SmallestSize).Open();
            entry.Write("{\"format\":\"commonage-export\",\"version\":8,\"items\":["u8);
            var spaces = new byte[1024 * 1024];
            Array.Fill(spaces, (byte)' ');
            for (var i = 0; i < 260; i++)
                entry.Write(spaces);
            entry.Write("]}"u8);
        }

        Assert.True(stream.Length < 5 * 1024 * 1024, "the test file is meant to be small");
        var html = await DryRunAsync(curator, stream.ToArray());

        Assert.Contains("too large to read", html);
        Assert.DoesNotContain("handler=Import", html);
    }

    [Fact]
    public async Task Whatever_the_status_an_item_arrives_as_a_draft_and_keeps_what_visitors_get_and_when()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        var json = ArchiveJson(
            [Item(1, $"{word} Embargoed", mediaLevel: "RecordOnly", publicFrom: "2099-06-01"),
             Item(2, $"{word} Accepted", status: "Submitted"),
             Item(3, $"{word} Wrong day", publicFrom: "1066-01-01"),
             Item(4, $"{word} Unknown level", mediaLevel: "Everything-and-more"),
             Item(5, $"{word} Unknown status", status: "Banana")],
            [Collection(1, $"{word} Collection")]);

        var html = await RunAsync(curator, Zip(("archive.json", json)));

        var items = await ItemsAsync(word);
        Assert.All(items, item => Assert.Equal(ItemStatus.Draft, item.Status));
        Assert.All(items, item => Assert.Null(item.PublishedAt));
        var embargoed = items.Single(i => i.Title.EndsWith("Embargoed"));
        Assert.Equal(MediaLevel.RecordOnly, embargoed.MediaLevel);
        Assert.Equal(new DateOnly(2099, 6, 1), embargoed.PublicFrom);
        Assert.Equal(MediaLevel.Everything, items.Single(i => i.Title.EndsWith("Accepted")).MediaLevel);
        Assert.Equal(2, items.Count);
        Assert.Contains("This value is not valid.", html);
        Assert.Contains("This value is not one this archive knows.", html);
    }

    [Fact]
    public async Task Rejected_withdrawn_and_unconsented_items_are_left_out_and_listed_with_a_reason()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        var json = ArchiveJson(
            [Item(1, $"{word} Fine"),
             Item(2, $"{word} Rejected", status: "Rejected"),
             Item(3, $"{word} Withdrawn", status: "Withdrawn"),
             Item(4, $"{word} No consent", contribution: new { consentCoversSharing = false }),
             Item(5, $"{word} Consent", contribution: new { consentCoversSharing = true })],
            [Collection(1, $"{word} Collection")]);

        var html = await RunAsync(curator, Zip(("archive.json", json)));

        Assert.Equal([$"{word} Consent", $"{word} Fine"], (await ItemsAsync(word)).Select(i => i.Title).Order().ToArray());
        Assert.Contains("it was rejected in the other archive", html);
        Assert.Contains("withdrew their consent", html);
        Assert.Contains("did not agree to it being shared further", html);
    }

    [Fact]
    public async Task A_contribution_that_does_not_say_yes_is_left_out_whether_it_says_no_nothing_or_null()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        var json = ArchiveJson(
            [Item(1, $"{word} Fine"),
             Item(2, $"{word} No key", contribution: new { }),
             Item(3, $"{word} Null answer", contribution: new { consentCoversSharing = (bool?)null }),
             Item(4, $"{word} Other keys only", contribution: new { showName = true })],
            [Collection(1, $"{word} Collection")]);

        var html = await RunAsync(curator, Zip(("archive.json", json)));

        Assert.Equal([$"{word} Fine"], (await ItemsAsync(word)).Select(i => i.Title));
        Assert.Equal(3, Regex.Matches(html, "did not agree to it being shared further").Count);
    }

    [Fact]
    public async Task A_photo_that_cannot_be_stored_costs_only_that_photo_and_the_item_stays_a_draft_with_the_others()
    {
        var word = Word();
        var storage = new StorageThatFailsOnOriginal(app.Services.GetRequiredService<IFileStorage>(), failOn: 2);
        await using var archive = await app.NewArchiveAsync(b => b.ConfigureTestServices(services => services.AddSingleton<IFileStorage>(storage)));
        using var curator = await CuratorAsync(archive);
        byte[][] photos = [NewPhoto(), NewPhoto(), NewPhoto()];
        var json = ArchiveJson(
            [Item(1, $"{word} Barn", files: [File(7, 1, "a.jpg", photos[0]), File(8, 1, "b.jpg", photos[1]), File(9, 1, "c.jpg", photos[2])])],
            [Collection(1, $"{word} Collection")]);
        var zip = Zip(("archive.json", json), ("files/1/7-a.jpg", photos[0]), ("files/1/8-b.jpg", photos[1]), ("files/1/9-c.jpg", photos[2]));

        var dryRun = await DryRunAsync(curator, zip);
        var response = await curator.PostAsync($"{Page}?handler=Import", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Browser.Token(dryRun),
            ["StagingId"] = StagingIdPattern().Match(dryRun).Groups[1].Value,
        }));
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("Photos that could not be added: 1", body);
        Assert.DoesNotContain("secret-disk-path", body);
        var item = Assert.Single(await archive.WithDbAsync(db => db.ArchiveItems.IgnoreQueryFilters().Include(i => i.MediaFiles).ToListAsync()));
        Assert.Equal(ItemStatus.Draft, item.Status);
        Assert.Equal([Sha(photos[0]), Sha(photos[2])], item.MediaFiles.OrderBy(f => f.Position).Select(f => f.Sha256));
        var actions = await archive.WithDbAsync(db => db.AuditEntries.Select(e => e.Action).ToListAsync());
        Assert.Contains(AuditAction.PhotosAdded, actions);
        Assert.Equal(1, ImportLock.OneAtATime.CurrentCount);

        // The item is there now, so a second run does not top it up, and the dry run says so.
        var again = await DryRunAsync(curator, zip);
        Assert.DoesNotContain("handler=Import", again);
        Assert.Contains("photos it lacks are not added", again);
        Assert.Equal(2, (await archive.WithDbAsync(db => db.MediaFiles.CountAsync())));
    }

    private sealed class StorageThatFailsOnOriginal(IFileStorage real, int failOn) : IFileStorage
    {
        private int _originals;

        public Task SaveAsync(string key, Stream content, CancellationToken cancellationToken = default) =>
            key.StartsWith("originals/", StringComparison.Ordinal) && Interlocked.Increment(ref _originals) == failOn
                ? throw new IOException("secret-disk-path is full.")
                : real.SaveAsync(key, content, cancellationToken);

        public Stream OpenRead(string key) => real.OpenRead(key);

        public void Delete(string key) => real.Delete(key);
    }

    /// <summary>Stores through the real storage, but fails the saves of originals that the script names, and tells what was saved and removed.</summary>
    private sealed class ScriptedStorage(IFileStorage real, Func<int, Exception?> failure, Action<int>? afterSave = null) : IFileStorage
    {
        private int _originals;

        public List<string> Saved { get; } = [];

        public List<string> Deleted { get; } = [];

        public async Task SaveAsync(string key, Stream content, CancellationToken cancellationToken = default)
        {
            if (!key.StartsWith("originals/", StringComparison.Ordinal))
            {
                await real.SaveAsync(key, content, cancellationToken);
                return;
            }

            var number = Interlocked.Increment(ref _originals);
            if (failure(number) is { } exception)
                throw exception;
            await real.SaveAsync(key, content, cancellationToken);
            Saved.Add(key);
            afterSave?.Invoke(number);
        }

        public Stream OpenRead(string key) => real.OpenRead(key);

        public bool Exists(string key) => real.Exists(key);

        public void Delete(string key)
        {
            Deleted.Add(key);
            real.Delete(key);
        }
    }

    /// <summary>Makes the save of a new photo fail the way a lost database connection does.</summary>
    private sealed class FailingPhotoSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            eventData.Context!.ChangeTracker.Entries<MediaFile>().Any(e => e.State == EntityState.Added)
                ? throw new DbUpdateException("secret-db-detail")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationTokenSource Stopping { get; } = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => Stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopping.Cancel();
    }

    private static string Copy(string word, int number) => $"{word} {number}";

    private (byte[] Zip, byte[][] Photos) ManyItems(string word, int items, int filesPerItem = 1)
    {
        var photos = Enumerable.Range(0, items * filesPerItem).Select(_ => NewPhoto()).ToArray();
        var entries = new List<(string, byte[])>();
        var all = new List<object>();
        for (var i = 1; i <= items; i++)
        {
            var files = new List<object>();
            for (var f = 1; f <= filesPerItem; f++)
            {
                var photo = photos[(i - 1) * filesPerItem + f - 1];
                var id = i * 100 + f;
                files.Add(File(id, i, $"p{id}.jpg", photo));
                entries.Add(($"files/{i}/{id}-p{id}.jpg", photo));
            }

            all.Add(Item(i, Copy(word, i), files: [.. files]));
        }

        return (Zip([("archive.json", ArchiveJson([.. all], [Collection(1, $"{word} Collection")])), .. entries]), photos);
    }

    private static async Task<ExportImportResult> RunDirectAsync(WebApplicationFactory<Program> archive, byte[] zip, IHostApplicationLifetime? lifetime = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "commonage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var zipPath = Path.Combine(folder, "export.zip");
            await System.IO.File.WriteAllBytesAsync(zipPath, zip);
            using var source = ImportSources.OpenZip(zipPath, Path.Combine(folder, "work"))!;
            using var scope = archive.Services.CreateScope();
            var import = lifetime is null
                ? scope.ServiceProvider.GetRequiredService<ExportImport>()
                : ActivatorUtilities.CreateInstance<ExportImport>(scope.ServiceProvider, lifetime);
            var plan = await import.PlanAsync(source, CancellationToken.None);
            Assert.True(plan.CanImport);
            return await import.RunAsync(plan, source, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static Task<HttpResponseMessage> PostImportAsync(HttpClient client, string dryRunHtml) =>
        client.PostAsync($"{Page}?handler=Import", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Browser.Token(dryRunHtml),
            ["StagingId"] = StagingIdPattern().Match(dryRunHtml).Groups[1].Value,
        }));

    private Task<string?> SummaryAsync(WebApplicationFactory<Program> archive) =>
        archive.WithDbAsync(db => db.AuditEntries.Where(e => e.Action == AuditAction.ArchiveImported).Select(e => e.Subject).SingleAsync());

    [Fact]
    public async Task A_run_that_keeps_failing_stops_instead_of_creating_empty_drafts_and_says_how_many_items_it_did_not_try()
    {
        var word = Word();
        var storage = new ScriptedStorage(app.Services.GetRequiredService<IFileStorage>(), _ => new IOException("secret-disk-path is full."));
        await using var archive = await app.NewArchiveAsync(b => b.ConfigureTestServices(services => services.AddSingleton<IFileStorage>(storage)));
        using var curator = await CuratorAsync(archive);
        var (zip, _) = ManyItems(word, ExportImport.StopAfterFailures + 3);

        var dryRun = await DryRunAsync(curator, zip);
        var response = await PostImportAsync(curator, dryRun);
        var body = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        Assert.Contains("The import stopped early", body);
        Assert.Contains("3 items were not tried", body);
        Assert.DoesNotContain("secret-disk-path", body);
        Assert.Equal(ExportImport.StopAfterFailures, await archive.WithDbAsync(db => db.ArchiveItems.CountAsync()));
        Assert.Equal($"{ExportImport.StopAfterFailures} / 0", await SummaryAsync(archive));
    }

    [Fact]
    public async Task One_good_file_between_failures_keeps_a_run_going()
    {
        var word = Word();
        var storage = new ScriptedStorage(app.Services.GetRequiredService<IFileStorage>(), n => n % 2 == 1 ? new IOException("Full.") : null);
        await using var archive = await app.NewArchiveAsync(b => b.ConfigureTestServices(services => services.AddSingleton<IFileStorage>(storage)));
        var (zip, _) = ManyItems(word, ExportImport.StopAfterFailures * 2);

        var result = await RunDirectAsync(archive, zip);

        Assert.Equal(ExportImport.StopAfterFailures * 2, result.Items);
        Assert.Equal(0, result.ItemsNotTried);
        Assert.Equal(ExportImport.StopAfterFailures, result.FilesFailed);
    }

    [Fact]
    public async Task A_run_stops_between_items_when_the_app_is_shutting_down()
    {
        var word = Word();
        var lifetime = new FakeLifetime();
        var storage = new ScriptedStorage(app.Services.GetRequiredService<IFileStorage>(), _ => null, afterSave: n =>
        {
            if (n == 1)
                lifetime.Stopping.Cancel();
        });
        await using var archive = await app.NewArchiveAsync(b => b.ConfigureTestServices(services => services.AddSingleton<IFileStorage>(storage)));
        var (zip, photos) = ManyItems(word, 3);

        var result = await RunDirectAsync(archive, zip, lifetime);

        Assert.Equal(1, result.Items);
        Assert.Equal(1, result.Files);
        Assert.Equal(2, result.ItemsNotTried);
        Assert.Equal([Sha(photos[0])], await archive.WithDbAsync(db => db.MediaFiles.Select(f => f.Sha256).ToListAsync()));
        Assert.Equal("1 / 1", await SummaryAsync(archive));
    }

    [Fact]
    public async Task A_run_that_is_cut_short_after_an_added_photo_still_logs_the_photos_and_the_summary_and_rethrows_the_real_error()
    {
        var word = Word();
        var storage = new ScriptedStorage(app.Services.GetRequiredService<IFileStorage>(), n => n == 2 ? new InvalidOperationException("Not a disk problem.") : null);
        await using var archive = await app.NewArchiveAsync(b => b.ConfigureTestServices(services => services.AddSingleton<IFileStorage>(storage)));
        var (zip, _) = ManyItems(word, 1, filesPerItem: 2);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => RunDirectAsync(archive, zip));

        Assert.Equal("Not a disk problem.", thrown.Message);
        var actions = await archive.WithDbAsync(db => db.AuditEntries.Select(e => e.Action).ToListAsync());
        Assert.Contains(AuditAction.PhotosAdded, actions);
        Assert.Equal("1 / 1", await SummaryAsync(archive));
        Assert.Equal(1, await archive.WithDbAsync(db => db.MediaFiles.CountAsync()));
    }

    [Fact]
    public async Task The_lock_is_released_and_the_copy_removed_when_a_run_fails_in_a_way_that_is_not_a_failed_file()
    {
        var word = Word();
        var storage = new ScriptedStorage(app.Services.GetRequiredService<IFileStorage>(), _ => new InvalidOperationException("secret-bug-detail"));
        await using var archive = await app.NewArchiveAsync(b => b.ConfigureTestServices(services => services.AddSingleton<IFileStorage>(storage)));
        using var curator = await CuratorAsync(archive);
        var (zip, _) = ManyItems(word, 2);
        var dryRun = await DryRunAsync(curator, zip);
        var id = Guid.Parse(StagingIdPattern().Match(dryRun).Groups[1].Value);

        var response = await PostImportAsync(curator, dryRun);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, ImportLock.OneAtATime.CurrentCount);
        Assert.False(archive.Services.GetRequiredService<ImportStaging>().ExportExists(id));
        Assert.Equal("1 / 0", await SummaryAsync(archive));
    }

    [Fact]
    public async Task A_photo_whose_save_fails_leaves_no_stored_original_and_is_counted_and_logged_against_its_item()
    {
        var word = Word();
        var storage = new ScriptedStorage(app.Services.GetRequiredService<IFileStorage>(), _ => null);
        var failing = new FailingPhotoSave();
        await using var archive = await app.NewArchiveAsync(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton<IFileStorage>(storage);
            services.ConfigureDbContext<CommonageDbContext>(options => options.AddInterceptors(failing));
        }));
        var (zip, _) = ManyItems(word, 1);

        var result = await RunDirectAsync(archive, zip);

        Assert.Equal(new ExportImportResult(1, 0, 1, 1), result);
        var key = Assert.Single(storage.Saved);
        Assert.Equal([key], storage.Deleted);
        Assert.False(storage.Exists(key));
        var line = await archive.WithDbAsync(db => db.AuditEntries.Where(e => e.Action == AuditAction.ImportPhotosMissing).SingleAsync());
        Assert.Equal(Copy(word, 1), line.Subject);
        Assert.NotNull(line.ArchiveItemId);
        Assert.DoesNotContain(AuditAction.PhotosAdded, await archive.WithDbAsync(db => db.AuditEntries.Select(e => e.Action).ToListAsync()));
        Assert.Equal("1 / 0", await SummaryAsync(archive));
    }

    [Fact]
    public async Task The_text_and_the_turn_of_a_photo_are_stored_with_it_in_one_save()
    {
        var word = Word();
        await using var archive = await app.NewArchiveAsync();
        var photo = NewPhoto();
        var json = ArchiveJson(
            [Item(1, $"{word} Barn", files: [File(7, 1, "a.jpg", photo, altText: "A red barn", caption: "Before the fire", rotation: 90)])],
            [Collection(1, $"{word} Collection")]);

        var result = await RunDirectAsync(archive, Zip(("archive.json", json), ("files/1/7-a.jpg", photo)));

        Assert.Equal(new ExportImportResult(1, 1, 0, 1), result);
        var stored = await archive.WithDbAsync(db => db.MediaFiles.SingleAsync());
        Assert.Equal(("A red barn", "Before the fire", 90), (stored.AltText, stored.Caption, stored.Rotation));
    }

    private static object[] EmptyFiles(int count) => Enumerable.Repeat<object>(new { }, count).ToArray();

    [Fact]
    public async Task More_files_in_all_than_the_cap_are_refused_even_when_no_item_has_too_many()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        var items = Enumerable.Range(1, ExportImport.MaxTotalFiles / ExportImport.MaxFilesPerItem + 1)
            .Select(i => Item(i, $"{word} {i}", files: EmptyFiles(ExportImport.MaxFilesPerItem))).ToArray();

        var html = await DryRunAsync(curator, Zip(("archive.json", ArchiveJson(items, [Collection(1, $"{word} Collection")]))));

        Assert.Contains("more items, files, tags, people or collections than one import can read", html);
        Assert.DoesNotContain("handler=Import", html);
    }

    [Fact]
    public async Task A_dry_run_stops_recording_lines_at_a_limit_counts_the_rest_and_still_leaves_out_every_item_with_a_problem()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        var items = Enumerable.Range(1, ExportImport.MaxTotalFiles / ExportImport.MaxFilesPerItem)
            .Select(i => Item(i, $"{word} {i}", files: EmptyFiles(ExportImport.MaxFilesPerItem))).ToArray();

        var html = await DryRunAsync(curator, Zip(("archive.json", ArchiveJson(items, [Collection(1, $"{word} Collection")]))));

        Assert.Contains("more things found, which are not listed", html);
        Assert.Contains($"Items with problems, which are left out: {items.Length}", html);
        Assert.DoesNotContain("handler=Import", html);
    }

    [Fact]
    public async Task A_second_run_of_the_same_export_adds_nothing()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        var zip = Zip(("archive.json", ArchiveJson([Item(1, $"{word} Barn"), Item(2, $"{word} Shed")], [Collection(1, $"{word} Collection")])));

        await RunAsync(curator, zip);
        var second = await RunAsync(curator, zip);

        Assert.Equal(2, (await ItemsAsync(word)).Count);
        Assert.DoesNotContain("handler=Import", second);
        Assert.Single((await ItemsAsync(word)).Select(i => i.CollectionId).Distinct());
    }

    [Fact]
    public async Task Titles_are_compared_after_normalising_so_a_decomposed_title_is_the_same_item()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        var collection = Collection(1, $"{word} Collection");
        await RunAsync(curator, Zip(("archive.json", ArchiveJson([Item(1, $"{word} Café")], [collection]))));

        var again = await RunAsync(curator, Zip(("archive.json", ArchiveJson([Item(1, $"{word} CAFÉ")], [collection]))));

        Assert.Single(await ItemsAsync(word));
        Assert.DoesNotContain("handler=Import", again);
    }

    [Fact]
    public async Task Titles_in_any_script_arrive_as_written()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        string[] titles = [$"春祭り {word}", $"مهرجان الحصاد {word}", $"Праздник урожая {word}"];
        var json = ArchiveJson(titles.Select((t, i) => Item(i + 1, t, description: t)).ToArray(), [Collection(1, $"{word} 収穫")]);

        await RunAsync(curator, Zip(("archive.json", json)));

        var items = await ItemsAsync(word);
        Assert.Equal(titles.Order(StringComparer.Ordinal), items.Select(i => i.Title).Order(StringComparer.Ordinal));
        Assert.All(items, item => Assert.Equal(item.Title, item.Description));
        Assert.All(items, item => Assert.Equal($"{word} 収穫", item.Collection.Title));
    }

    [Fact]
    public async Task A_date_text_nobody_can_read_still_arrives_with_its_exported_days()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        var json = ArchiveJson(
            [Item(1, $"{word} Barn", date: new { text = "around the time of the old mill", from = "1950-01-01", to = "1959-12-31" })],
            [Collection(1, $"{word} Collection")]);

        await RunAsync(curator, Zip(("archive.json", json)));

        var item = Assert.Single(await ItemsAsync(word));
        Assert.Equal(new DateOnly(1950, 1, 1), item.DateFrom);
        Assert.Equal(new DateOnly(1959, 12, 31), item.DateTo);
        Assert.Equal("around the time of the old mill", item.DateText);
    }

    [Fact]
    public async Task Only_an_admin_may_use_the_page()
    {
        using var admin = await CuratorAsync(app);
        var (curator, _, _, _) = await app.CreateAccountAsync(admin);
        using var anonymous = app.CreateClient(new() { AllowAutoRedirect = false });

        Assert.Equal(HttpStatusCode.Forbidden, (await curator.GetAsync(Page)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(Page)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync(Page)).StatusCode);
        curator.Dispose();
    }

    [Fact]
    public void Every_property_of_the_export_has_a_decision_and_the_reader_holds_exactly_what_is_not_dropped()
    {
        var assembly = typeof(ArchiveExporter).Assembly;
        string[] types = ["ExportSettings", "ExportText", "ExportCollection", "ExportTag", "ExportItem", "ExportDate", "ExportContribution", "ExportNote", "ExportFile", "ExportStory", "ExportStoryBlock", "ExportPerson", "ExportPersonName"];
        var missing = new List<string>();
        var found = new HashSet<string>();
        foreach (var name in types)
        {
            var type = assembly.GetTypes().Single(t => t.Name == name && t.Namespace == typeof(ArchiveExporter).Namespace);
            foreach (var property in type.GetProperties().Where(p => p.DeclaringType == type))
            {
                var key = $"{name}.{property.Name}";
                found.Add(key);
                if (!ExportImportTable.Fields.ContainsKey(key))
                    missing.Add(key);
            }
        }

        Assert.True(missing.Count == 0, "The export has properties that ExportImportTable has no decision for:\n" + string.Join('\n', missing));
        Assert.True(ExportImportTable.Fields.Keys.All(found.Contains), "ExportImportTable decides about a property that the export no longer has.");

        (string Export, Type Imported)[] pairs =
        [
            ("ExportCollection", typeof(ImportedCollection)), ("ExportTag", typeof(ImportedTag)), ("ExportItem", typeof(ImportedItem)),
            ("ExportPerson", typeof(ImportedPerson)), ("ExportPersonName", typeof(ImportedPersonName)), ("ExportDate", typeof(ImportedDate)), ("ExportContribution", typeof(ImportedContribution)), ("ExportFile", typeof(ImportedFile)),
        ];
        foreach (var (export, imported) in pairs)
        {
            var wanted = ExportImportTable.Fields
                .Where(f => f.Key.StartsWith(export + ".") && f.Value.Decision != ExportFieldDecision.Dropped)
                .Select(f => f.Key[(export.Length + 1)..]).Order(StringComparer.Ordinal).ToArray();
            var held = imported.GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(wanted, held);
        }

        var contribution = ExportImportTable.Fields.Where(f => f.Key.StartsWith("ExportContribution.") && f.Value.Decision != ExportFieldDecision.Dropped).ToList();
        Assert.Equal("ExportContribution.ConsentCoversSharing", Assert.Single(contribution).Key);
    }

    private static object PersonJson(int id, string? name, object? died = null, object[]? names = null, string sortName = "Sorted, Name") => new
    {
        id,
        publicId = "ABCDEFGHJKMN",
        name,
        sortName,
        disclosure = "Deceased",
        born = (object?)null,
        died,
        biography = "A life",
        curatorNote = "Secret note of the other archive",
        createdAt = DateTime.UtcNow,
        names = names ?? [],
    };

    [Fact]
    public async Task People_arrive_as_new_hidden_people_with_their_other_names_hidden_and_are_never_matched_with_people_here()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        var sameName = $"{word} Karl";
        var existing = await app.WithDbAsync(async db =>
        {
            var here = db.AddHiddenPerson(sameName, DateTime.UtcNow);
            here.Biography = "What this archive knows";
            here.SetDisclosure(PersonDisclosure.Deceased, DateTime.UtcNow);
            await db.SaveChangesAsync();
            return here.Id;
        });
        object[] names = [new { name = $"{word} Karlchen", kind = "Nickname", language = "de", shownToVisitors = true }];
        object[] people = [PersonJson(7, sameName, new { text = "1990", from = "1990-01-01", to = "1990-12-31" }, names), PersonJson(8, $"{word} Nobody names me")];
        var zip = Zip(("archive.json", ArchiveJson([Item(1, $"{word} Goalkeeper", people: [7, 7, 99])], [Collection(1, $"{word} Club")], 10, people: people)));

        var html = await DryRunAsync(curator, zip);
        Assert.Contains("1 person would be created, who only curators see.", html);
        await ImportAsync(curator, html);

        var imported = await app.WithDbAsync(db => db.People.Include(p => p.Names).Where(p => p.Id != existing && p.Name.StartsWith(word)).ToListAsync());
        var person = Assert.Single(imported);
        Assert.Equal((sameName, PersonDisclosure.Hidden, null, "A life", "Sorted, Name"), (person.Name, person.Disclosure, person.CuratorNote, person.Biography, person.SortName));
        Assert.NotEqual("ABCDEFGHJKMN", person.PublicId);
        Assert.Equal("1990", person.DiedText);
        var other = Assert.Single(person.Names);
        Assert.Equal(($"{word} Karlchen", PersonNameKind.Nickname, "de", false), (other.Name, other.Kind, other.Language, other.ShownToVisitors));
        var here2 = await app.WithDbAsync(db => db.People.SingleAsync(p => p.Id == existing));
        Assert.Equal(("What this archive knows", PersonDisclosure.Deceased), (here2.Biography, here2.Disclosure));
        var item = Assert.Single(await ItemsAsync(word + " Goalkeeper"));
        Assert.Equal([person.Id], await app.WithDbAsync(db => db.ItemPeople.Where(l => l.ArchiveItemId == item.Id).Select(l => l.PersonId).ToListAsync()));
    }

    [Fact]
    public async Task Person_tags_of_an_older_export_arrive_as_new_hidden_people_that_wait_for_review_even_when_a_person_of_that_name_is_here()
    {
        using var curator = await CuratorAsync(app);
        var word = Word()[..12]; // a tag name is at most 50 characters
        var sameName = $"{word} Tagged Karl";
        var existing = await app.WithDbAsync(async db =>
        {
            var here = db.AddHiddenPerson(sameName, DateTime.UtcNow);
            await db.SaveChangesAsync();
            return here.Id;
        });
        var item = new
        {
            id = 1, collectionId = 1, title = $"{word} Team", description = (string?)null, status = "Published", date = (object?)null,
            tags = new[] { sameName, $"{word} Tagged Berta", $"{word} Mill Street" }, people = Array.Empty<int>(), mediaLevel = (string?)null, publicFrom = (string?)null, contribution = (object?)null, files = Array.Empty<object>(),
        };
        var tags = new object[] { new { name = sameName, kind = "Person" }, new { name = $"{word} Tagged Berta", kind = "Person" }, new { name = $"{word} Mill Street", kind = "Place" } };
        var json = JsonSerializer.SerializeToUtf8Bytes(new { format = "commonage-export", version = 9, collections = new[] { Collection(1, $"{word} Club") }, tags, items = new[] { item }, people = Array.Empty<object>() }, Json);

        var html = await DryRunAsync(curator, Zip(("archive.json", json)));
        Assert.Contains("2 people would be created, who only curators see.", html);
        await ImportAsync(curator, html);

        var created = await app.WithDbAsync(db => db.People.Where(p => p.Name.StartsWith(word + " Tagged") && p.Id != existing).OrderBy(p => p.Name).ToListAsync());
        Assert.Equal([$"{word} Tagged Berta", sameName], created.Select(p => p.Name));
        Assert.All(created, p => Assert.Equal(PersonDisclosure.Hidden, p.Disclosure));
        // Hidden and chosen: they do not wait on the review list, since nothing was carried over unreviewed.
        Assert.All(created, p => Assert.NotNull(p.DisclosureChangedAt));
        var importedItem = Assert.Single(await ItemsAsync(word + " Team"));
        Assert.Equal(created.Select(p => p.Id).Order(), (await app.WithDbAsync(db => db.ItemPeople.Where(l => l.ArchiveItemId == importedItem.Id).Select(l => l.PersonId).ToListAsync())).Order());
        // The place stays a tag, the person tags are not tags here.
        var tagNames = await app.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == importedItem.Id).SelectMany(i => i.Tags).Select(t => t.Name).ToListAsync());
        Assert.Equal([$"{word} Mill Street"], tagNames);
    }

    [Fact]
    public async Task Imported_people_are_one_line_in_the_activity_log_with_their_number()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        object[] people = [PersonJson(1, $"{word} One"), PersonJson(2, $"{word} Two"), PersonJson(3, $"{word} Three")];
        var zip = Zip(("archive.json", ArchiveJson([Item(1, $"{word} Item", people: [1, 2, 3])], [Collection(1, $"{word} Collection")], 10, people: people)));
        var started = DateTime.UtcNow.AddSeconds(-1);

        await RunAsync(curator, zip);

        var ids = await app.WithDbAsync(db => db.People.Where(p => p.Name.StartsWith(word)).Select(p => p.Id).ToListAsync());
        Assert.Equal(3, ids.Count);
        var entries = await app.WithDbAsync(db => db.AuditEntries.Where(e => e.At >= started && e.Action == AuditAction.PeopleImported || e.PersonId != null && ids.Contains(e.PersonId.Value)).ToListAsync());
        var line = Assert.Single(entries);
        Assert.Equal((AuditAction.PeopleImported, "3", null), (line.Action, line.Subject, line.PersonId));
    }

    [Fact]
    public async Task Imported_names_are_cleaned_like_names_typed_in_the_form()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        // "e" and "a" with a combining accent, padded and spaced twice: the same as the composed letters, once cleaned.
        var messy = $"  {word}  José   Márquez ";
        object[] others = [new { name = "  José	 María ", kind = "Nickname", shownToVisitors = false }];
        var zip = Zip(("archive.json", ArchiveJson([Item(1, $"{word} Item", people: [1])], [Collection(1, $"{word} Collection")], 10,
            people: [PersonJson(1, messy, names: others, sortName: " Márquez ,  José ")])));

        await RunAsync(curator, zip);

        var person = await app.WithDbAsync(db => db.People.Include(p => p.Names).SingleAsync(p => p.Name.Contains(word)));
        Assert.Equal($"{word} José Márquez", person.Name);
        Assert.Equal("Márquez , José", person.SortName);
        Assert.Equal("José María", Assert.Single(person.Names).Name);
        Assert.True(person.Name.IsNormalized());
    }

    [Fact]
    public async Task A_person_without_a_usable_name_or_with_too_much_is_left_out_and_nothing_else_goes_wrong()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        object[] people =
        [
            PersonJson(1, "   "), PersonJson(2, null), PersonJson(3, new string('x', 500)),
            PersonJson(4, $"{word} Fine", new { text = "not a date at all !!", from = "x", to = "y" },
                Enumerable.Range(0, 20).Select(i => (object)new { name = $"{word} other {i}", kind = "Nonsense", language = "not a code!", shownToVisitors = true }).ToArray()),
        ];
        var zip = Zip(("archive.json", ArchiveJson([Item(1, $"{word} Item", people: [1, 2, 3, 4])], [Collection(1, $"{word} Collection")], 10, people: people)));

        await RunAsync(curator, zip);

        var imported = await app.WithDbAsync(db => db.People.Include(p => p.Names).Where(p => p.Name.StartsWith(word) || p.Name.StartsWith("xxxx")).ToListAsync());
        var fine = Assert.Single(imported);
        Assert.Null(fine.DiedText);
        Assert.True(fine.Names.Count <= Commonage.Web.Domain.Person.MaxNames);
        Assert.All(fine.Names, n => Assert.False(n.ShownToVisitors));
        Assert.All(fine.Names, n => Assert.Null(n.Language));
        Assert.Single(await ItemsAsync(word + " Item"));
    }

    [Fact]
    public async Task A_person_with_more_other_names_than_a_person_can_have_refuses_the_whole_export()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        var names = Enumerable.Range(0, Commonage.Web.Domain.Person.MaxNames + 1).Select(i => (object)new { name = $"{word} other {i}", kind = "Other" }).ToArray();
        var zip = Zip(("archive.json", ArchiveJson([Item(1, $"{word} Item", people: [1])], [Collection(1, $"{word} Collection")], 10, people: [PersonJson(1, $"{word} Many", names: names)])));

        var html = await RunAsync(curator, zip);

        Assert.Contains("more items, files, tags, people or collections", html);
        Assert.Empty(await ItemsAsync(word));
        Assert.False(await app.WithDbAsync(db => db.People.AnyAsync(p => p.Name.StartsWith(word))));
    }

    [Fact]
    public async Task An_older_export_without_people_imports_as_before()
    {
        using var curator = await CuratorAsync(app);
        var word = Word();
        var before = await app.WithDbAsync(db => db.People.CountAsync());

        var html = await RunAsync(curator, Zip(("archive.json", ArchiveJson([Item(1, $"{word} Old")], [Collection(1, $"{word} Collection")], 9))));

        Assert.DoesNotContain("would be created, who only curators see", html);
        Assert.Single(await ItemsAsync(word));
        Assert.Equal(before, await app.WithDbAsync(db => db.People.CountAsync()));
    }

    [Fact]
    public async Task A_helper_cannot_import_people_or_anything_else()
    {
        using var admin = await CuratorAsync(app);
        var (helper, _, _, _) = await app.CreateAccountAsync(admin, Commonage.Web.Identity.Roles.Helper);

        Assert.Equal(HttpStatusCode.Forbidden, (await helper.GetAsync(Page)).StatusCode);
    }

    [Fact]
    public void The_importer_never_publishes_deletes_or_reads_around_the_filters()
    {
        var folder = Path.Combine(SourceRoot(), "Import");
        string[] forbidden = ["ItemStatus.Published", ".Publish(", "PublishedAt", "SetPublicFrom", ".Remove(", "RemoveRange", "ExecuteDelete", "ExecuteUpdate", "IgnoreQueryFilters", "DateTime.UtcNow", "DateTime.Now"];
        var offences = new List<string>();
        // ExportReading.cs only names the dropped properties, among them PublishedAt, so it is the importer that is read.
        foreach (var file in new[] { "ExportImport.cs" })
        {
            var text = System.IO.File.ReadAllText(Path.Combine(folder, file));
            offences.AddRange(forbidden.Where(text.Contains).Select(f => $"{file}: {f}"));
        }

        Assert.True(offences.Count == 0, "The importer must make drafts, only insert, and use the clock it is given:\n" + string.Join('\n', offences));
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Commonage.Web")))
            directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Source folder not found."), "src", "Commonage.Web");
    }

    private static byte[] ReadEntry(byte[] zip, string path)
    {
        using var archive = new ZipArchive(new MemoryStream(zip));
        using var stream = archive.GetEntry(path)!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Changes the size that the directory of the ZIP file gives for one entry, as a made-up file would.</summary>
    private static void LieAboutSize(byte[] zip, string name, uint size)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        for (var i = 0; i + 46 + nameBytes.Length <= zip.Length; i++)
        {
            if (BitConverter.ToUInt32(zip, i) != 0x02014b50 || !zip.AsSpan(i + 46, nameBytes.Length).SequenceEqual(nameBytes))
                continue;
            BitConverter.GetBytes(size).CopyTo(zip, i + 24);
            return;
        }

        throw new InvalidOperationException("Entry not found.");
    }
}
