using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public partial class ImportTests(CommonageAppFactory app)
{
    private const string Page = "/Admin/Items/Import";

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

    private async Task<string> NewCollectionAsync()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
        var collection = new Collection { Title = $"Import {Guid.NewGuid():N}", CreatedAt = DateTime.UtcNow };
        db.Collections.Add(collection);
        await db.SaveChangesAsync();
        return collection.Title;
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

    private static string Sheet(params string[] rows) =>
        "collection,title,date,tags,files\n" + string.Join("\n", rows) + "\n";

    /// <summary>The first step: a spreadsheet and optionally a ZIP file or a folder. Returns the page that shows the dry run.</summary>
    private static async Task<(HttpStatusCode Status, string Html)> DryRunAsync(
        HttpClient client, string sheet, byte[]? zip = null, string? folder = null, string? collectionId = null)
    {
        var page = await client.GetStringAsync(Page);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Browser.Token(page)), "__RequestVerificationToken" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(sheet)), "sheet", "rows.csv" },
        };
        if (zip is not null)
            form.Add(new ByteArrayContent(zip), "scans", "scans.zip");
        form.Add(new StringContent(folder ?? ""), "Folder");
        form.Add(new StringContent(collectionId ?? ""), "CollectionId");
        var response = await client.PostAsync(Page, form);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> ImportAsync(HttpClient client, string dryRunHtml, string folder = "")
    {
        var id = StagingIdPattern().Match(dryRunHtml).Groups[1].Value;
        Assert.NotEmpty(id);
        var form = Browser.Token(dryRunHtml);
        return await client.PostAsync($"{Page}?handler=Import", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = form,
            ["StagingId"] = id,
            ["SheetName"] = "rows.csv",
            ["Folder"] = folder,
            ["CollectionId"] = "",
        }));
    }

    private Task<List<ArchiveItem>> ItemsAsync(string collectionTitle) =>
        InDatabaseAsync(db => db.ArchiveItems.IgnoreQueryFilters()
            .Include(i => i.Tags).Include(i => i.MediaFiles)
            .Where(i => i.Collection.Title == collectionTitle).OrderBy(i => i.Id).ToListAsync());

    [GeneratedRegex(@"name=""StagingId""[^>]*value=""([^""]+)""")]
    private static partial Regex StagingIdPattern();

    [Fact]
    public async Task A_dry_run_changes_nothing()
    {
        var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();
        var picture = NewPhoto();

        var (status, html) = await DryRunAsync(curator,
            Sheet($"{collection},Barn raising,1953,barn;harvest,barn.jpg"), Zip(("barn.jpg", picture)));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Nothing has been imported yet", html);
        Assert.Empty(await ItemsAsync(collection));
    }

    [Fact]
    public async Task Importing_creates_drafts_with_their_photos_and_tags()
    {
        var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();
        var tag = $"harvest{Guid.NewGuid():N}";

        var (_, html) = await DryRunAsync(curator,
            Sheet($"{collection},Barn raising,12.03.1953,{tag},barn.jpg", $"{collection},Empty field,,,"),
            Zip(("scans/barn.jpg", NewPhoto())));
        var response = await ImportAsync(curator, html);
        response.EnsureSuccessStatusCode();

        var items = await ItemsAsync(collection);
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal(ItemStatus.Draft, item.Status));
        var barn = items.Single(i => i.Title == "Barn raising");
        Assert.Single(barn.MediaFiles);
        Assert.Contains(barn.Tags, t => t.Name == tag);
        Assert.Equal(new DateOnly(1953, 3, 12), barn.DateFrom);
        Assert.Empty(items.Single(i => i.Title == "Empty field").MediaFiles);
    }

    [Fact]
    public async Task The_dry_run_reports_what_is_wrong_and_the_import_leaves_those_rows_out()
    {
        var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();

        var (_, html) = await DryRunAsync(curator, Sheet(
            $"{collection},Good one,1950,,",
            $"{collection},Bad date,not a date at all,,",
            "No such collection here,Orphan,1950,,",
            $"{collection},Lost file,1950,,missing.jpg"),
            Zip(("other.jpg", NewPhoto())));

        Assert.Contains("not a date at all", html);
        Assert.Contains("No such collection here", html);
        Assert.Contains("missing.jpg", html);
        Assert.Contains("Rows with problems, which are left out: 3", html);

        (await ImportAsync(curator, html)).EnsureSuccessStatusCode();
        Assert.Equal(["Good one"], (await ItemsAsync(collection)).Select(i => i.Title));
    }

    [Fact]
    public async Task Importing_the_same_sheet_again_leaves_out_what_is_already_there()
    {
        var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();
        var picture = NewPhoto();
        var sheet = Sheet($"{collection},Barn raising,1953,,barn.jpg");
        var zip = Zip(("barn.jpg", picture));

        var (_, first) = await DryRunAsync(curator, sheet, zip);
        (await ImportAsync(curator, first)).EnsureSuccessStatusCode();
        var (_, second) = await DryRunAsync(curator, sheet, zip);

        Assert.Contains("Rows that can be imported: 0", second);
        Assert.Single(await ItemsAsync(collection));
    }

    [Fact]
    public async Task Rows_of_one_sheet_that_repeat_each_other_are_imported_once()
    {
        var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();

        var (_, html) = await DryRunAsync(curator, Sheet($"{collection},Same,1950,,", $"{collection},Same,1950,,"));
        (await ImportAsync(curator, html)).EnsureSuccessStatusCode();

        Assert.Single(await ItemsAsync(collection));
    }

    [Fact]
    public async Task A_folder_on_the_media_volume_works_like_a_ZIP_file()
    {
        var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();
        var staging = app.Services.GetRequiredService<ImportStaging>();
        var name = $"batch-{Guid.NewGuid():N}";
        var folder = Path.Combine(staging.FoldersRoot, name);
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "barn.jpg"), NewPhoto());

        var (_, html) = await DryRunAsync(curator, Sheet($"{collection},Barn raising,1953,,barn.jpg"), folder: name);
        (await ImportAsync(curator, html, name)).EnsureSuccessStatusCode();

        Assert.Single((await ItemsAsync(collection)).Single().MediaFiles);
    }

    [Fact]
    public async Task Files_whose_names_differ_only_by_case_are_reported_not_a_server_error()
    {
        var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();

        var (status, html) = await DryRunAsync(curator, Sheet($"{collection},Barn,1953,,A.jpg"),
            Zip(("A.jpg", NewPhoto()), ("a.jpg", NewPhoto())));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Several files have this name", html);
    }

    [Theory]
    [InlineData(12)] // size of the central directory
    [InlineData(16)] // where it starts: all ones means "look in the ZIP64 record"
    public async Task A_ZIP_file_that_points_to_a_huge_central_directory_is_refused_before_it_is_read(int fieldOffset)
    {
        var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();
        var zip = Zip(("barn.jpg", NewPhoto()));
        var end = zip.AsSpan().LastIndexOf(new byte[] { 0x50, 0x4B, 5, 6 });
        BitConverter.GetBytes(uint.MaxValue).CopyTo(zip, end + fieldOffset);

        var (status, html) = await DryRunAsync(curator, Sheet($"{collection},Barn,1953,,barn.jpg"), zip);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("This is not a ZIP file, or it holds too many files.", html);
        Assert.Empty(await ItemsAsync(collection));
    }

    [Fact]
    public async Task Imported_photos_are_in_the_activity_log_as_photos_added()
    {
        var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();

        var (_, html) = await DryRunAsync(curator, Sheet($"{collection},Barn raising,1953,,barn.jpg"), Zip(("barn.jpg", NewPhoto())));
        (await ImportAsync(curator, html)).EnsureSuccessStatusCode();

        var item = (await ItemsAsync(collection)).Single();
        Assert.True(await InDatabaseAsync(db => db.AuditEntries.AnyAsync(a => a.Action == AuditAction.PhotosAdded && a.ArchiveItemId == item.Id)));
    }

    [Fact]
    public async Task A_folder_name_that_is_not_in_the_listing_is_refused()
    {
        var curator = await CuratorAsync();
        var collection = await NewCollectionAsync();

        var (status, html) = await DryRunAsync(curator, Sheet($"{collection},Barn raising,1953,,"), folder: "../..");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("This folder is not there any more.", html);
        Assert.Empty(await ItemsAsync(collection));
    }

    [Fact]
    public async Task A_sheet_that_is_not_UTF8_is_refused_before_any_letter_is_wrong()
    {
        var curator = await CuratorAsync();
        var page = await curator.GetStringAsync(Page);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(Browser.Token(page)), "__RequestVerificationToken" },
            { new ByteArrayContent(Encoding.Latin1.GetBytes("title\nMühle\n")), "sheet", "rows.csv" },
        };

        var response = await curator.PostAsync(Page, form);

        Assert.Contains("CSV UTF-8", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Visitors_and_the_template_download()
    {
        var visitor = app.CreateClient(new() { AllowAutoRedirect = false });
        Assert.NotEqual(HttpStatusCode.OK, (await visitor.GetAsync(Page)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await visitor.GetAsync($"{Page}?handler=Template")).StatusCode);

        var curator = await CuratorAsync();
        var template = await curator.GetAsync($"{Page}?handler=Template");
        template.EnsureSuccessStatusCode();
        Assert.Contains("collection", await template.Content.ReadAsStringAsync());
    }
}
