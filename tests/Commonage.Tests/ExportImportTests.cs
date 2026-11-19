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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

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

    private static byte[] ArchiveJson(object[] items, object[]? collections = null, int version = 8, string format = "commonage-export") =>
        JsonSerializer.SerializeToUtf8Bytes(new { format, version, collections = collections ?? [], tags = Array.Empty<object>(), items }, Json);

    private static object Collection(int id, string title) => new { id, title, description = (string?)null };

    private static object File(int id, int item, string name, byte[] content, string? sha = null, string? path = null) => new
    {
        id,
        path = path ?? $"files/{item}/{id}-{name}",
        originalFileName = name,
        sha256 = sha ?? Sha(content),
        position = 0,
        rotation = 0,
        problem = (string?)null,
    };

    private static object Item(int id, string title, int collectionId = 1, string status = "Published", object? date = null,
        object? contribution = null, object[]? files = null, string? mediaLevel = null, string? publicFrom = null, string? description = null) => new
    {
        id,
        collectionId,
        title,
        description,
        status,
        date,
        tags = Array.Empty<string>(),
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

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
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
    [InlineData(9, "format version 9")]
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
        string[] types = ["ExportSettings", "ExportText", "ExportCollection", "ExportTag", "ExportItem", "ExportDate", "ExportContribution", "ExportNote", "ExportFile"];
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
            ("ExportDate", typeof(ImportedDate)), ("ExportContribution", typeof(ImportedContribution)), ("ExportFile", typeof(ImportedFile)),
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
