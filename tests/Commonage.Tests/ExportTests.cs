using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class ExportTests(CommonageAppFactory app)
{
    [Fact]
    public async Task The_export_holds_every_original_and_all_descriptions()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Published, name: "Export Person", email: "export-person@example.org");
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        await Browser.UploadAsync(admin, $"/Admin/Items/Edit/{item.Id}", "Upload", "Flood: Mill Street?.jpg", TestData.Photo);
        var file = await app.WithDbAsync(db => db.MediaFiles.SingleAsync(f => f.ArchiveItemId == item.Id));

        using var zip = await ExportAsync(admin, publishedOnly: false, includePersonalData: true);

        var exported = FindItem(zip, item.Id);
        var exportedFile = exported.GetProperty("files")[0];
        var path = exportedFile.GetProperty("path").GetString()!;
        Assert.Equal($"files/{item.Id}/{file.Id}-Flood_ Mill Street_.jpg", path);
        Assert.Equal(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(Read(zip, path))));
        Assert.Equal(JsonValueKind.Null, exportedFile.GetProperty("problem").ValueKind);
        Assert.Equal("Flood: Mill Street?.jpg", exportedFile.GetProperty("originalFileName").GetString());
        Assert.Equal(item.Title, exported.GetProperty("title").GetString());
        Assert.Equal("Published", exported.GetProperty("status").GetString());
        Assert.Equal("export-person@example.org", exported.GetProperty("contribution").GetProperty("contributorEmail").GetString());
        Assert.Equal("Heinz Muster", exported.GetProperty("contribution").GetProperty("rightsHolder").GetString());
        Assert.Contains(item.Title, Encoding.UTF8.GetString(Read(zip, "items.csv")));
        Assert.NotNull(zip.GetEntry("README.txt"));
        Assert.Contains("Exported the archive with contributors", await admin.GetStringAsync("/Admin/Activity"));
    }

    [Fact]
    public async Task Without_personal_data_no_name_or_email_is_in_the_export()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Submitted, name: "Hidden Person", email: "hidden-person@example.org");
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);

        using var zip = await ExportAsync(admin, publishedOnly: false, includePersonalData: false);

        var contribution = FindItem(zip, item.Id).GetProperty("contribution");
        Assert.Equal(JsonValueKind.Null, contribution.GetProperty("contributorName").ValueKind);
        Assert.Equal("PermissionFromRightsHolder", contribution.GetProperty("rights").GetString());
        foreach (var entry in zip.Entries.Where(e => !e.FullName.StartsWith("files/")))
        {
            var text = Encoding.UTF8.GetString(Read(zip, entry.FullName));
            Assert.DoesNotContain("Hidden Person", text);
            Assert.DoesNotContain("hidden-person@example.org", text);
            Assert.DoesNotContain("Heinz Muster", text);
        }
    }

    [Fact]
    public async Task Items_in_the_bin_are_not_exported()
    {
        var binned = await app.CreateContributionAsync(ItemStatus.Published);
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var page = $"/Admin/Items/Edit/{binned.Id}";
        await Browser.PostFormAsync(admin, $"{page}?handler=Delete", new(), page);

        using var zip = await ExportAsync(admin, publishedOnly: false, includePersonalData: false);

        Assert.DoesNotContain(binned.Title, Encoding.UTF8.GetString(Read(zip, "archive.json")));
    }

    [Fact]
    public async Task A_published_only_export_leaves_out_drafts_and_contributions()
    {
        var published = await app.CreateContributionAsync(ItemStatus.Published);
        var waiting = await app.CreateContributionAsync(ItemStatus.Submitted);
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);

        using var zip = await ExportAsync(admin, publishedOnly: true, includePersonalData: false);

        var document = JsonDocument.Parse(Read(zip, "archive.json")).RootElement;
        var ids = document.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();
        var statuses = document.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("status").GetString()).Distinct();
        Assert.Contains(published.Id, ids);
        Assert.DoesNotContain(waiting.Id, ids);
        Assert.Equal(["Published"], statuses);
        Assert.Equal(1, document.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Text_sent_in_by_anyone_cannot_become_a_spreadsheet_formula()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Submitted);
        await app.WithDbAsync(async db =>
        {
            var stored = await db.ArchiveItems.SingleAsync(i => i.Id == item.Id);
            stored.Title = "=HYPERLINK(\"https://example.org\", \"Click\")";
            return await db.SaveChangesAsync();
        });
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);

        using var zip = await ExportAsync(admin, publishedOnly: false, includePersonalData: false);

        var csv = Encoding.UTF8.GetString(Read(zip, "items.csv"));
        Assert.Contains($"{item.Id},", csv);
        Assert.Contains("\"'=HYPERLINK(\"\"https://example.org\"\", \"\"Click\"\")\"", csv);
        Assert.Equal("=HYPERLINK(\"https://example.org\", \"Click\")", FindItem(zip, item.Id).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Only_admins_can_export()
    {
        using var admin = app.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (curator, _, _, _) = await app.CreateAccountAsync(admin);

        var response = await Browser.SendFormAsync(curator, "/Admin/Export", new(), pageUrl: "/Admin");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual("application/zip", response.Content.Headers.ContentType?.MediaType);
        curator.Dispose();
    }

    private static async Task<ZipArchive> ExportAsync(HttpClient admin, bool publishedOnly, bool includePersonalData)
    {
        var fields = new Dictionary<string, string>();
        if (publishedOnly)
            fields["PublishedOnly"] = "true";
        if (includePersonalData)
            fields["IncludePersonalData"] = "true";
        var response = await Browser.PostFormAsync(admin, "/Admin/Export", fields);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("commonage-export-", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        return new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
    }

    private static JsonElement FindItem(ZipArchive zip, int id) =>
        JsonDocument.Parse(Read(zip, "archive.json")).RootElement.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetInt32() == id);

    private static byte[] Read(ZipArchive zip, string path)
    {
        using var stream = zip.GetEntry(path)!.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
