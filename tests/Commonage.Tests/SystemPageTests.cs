using System.Net;
using Commonage.Web;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Tests;

/// <summary>The System page, in an archive of its own, so that only its own photos are counted and retried.</summary>
[Collection(nameof(AppCollection))]
public class SystemPageTests(CommonageAppFactory app)
{
    private static async Task<int> UploadAndWaitAsync(WebApplicationFactory<Program> archive, HttpClient admin, string fileName)
    {
        var item = await archive.CreateContributionAsync(ItemStatus.Submitted);
        await Browser.UploadAsync(admin, $"/Admin/Items/Edit/{item.Id}", "Upload", fileName, TestData.Photo);
        return await WaitUntilReadyAsync(archive, item.Id);
    }

    private static async Task<int> WaitUntilReadyAsync(WebApplicationFactory<Program> archive, int itemId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var file = await archive.WithDbAsync(db => db.MediaFiles.AsNoTracking().SingleAsync(f => f.ArchiveItemId == itemId));
            if (file.Status == ProcessingStatus.Ready)
                return file.Id;
            Assert.True(DateTime.UtcNow < deadline, $"The preview was not made in time: {file.Status} {file.ProcessingError}");
            await Task.Delay(100);
        }
    }

    private static Task FailAsync(WebApplicationFactory<Program> archive, int fileId) =>
        archive.WithDbAsync(db => db.MediaFiles.Where(f => f.Id == fileId).ExecuteUpdateAsync(s => s
            .SetProperty(f => f.Status, ProcessingStatus.Failed)
            .SetProperty(f => f.ProcessingError, "VipsJpeg: Premature end of input file")));

    [Fact]
    public async Task An_admin_sees_the_version_the_checks_the_disk_and_the_database()
    {
        await using var archive = await app.NewArchiveAsync();
        using var admin = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        await UploadAndWaitAsync(archive, admin, "harbour.jpg");

        var page = await admin.GetStringAsync("/Admin/System");

        Assert.Contains($"Commonage {AppVersion.Current}", page);
        Assert.Contains("/health answers:", page);
        Assert.Contains("Media folder", page);
        Assert.Contains("in 1 file.", page);
        Assert.Contains("PostgreSQL", page);
        Assert.Contains("_AddRetention", page);
        Assert.True(page.Contains("The PostgreSQL extension unaccent") || page.Contains("A built-in folding"), "The page says how search ignores accents.");
        Assert.Contains("No previews have failed.", page);
        Assert.Matches(@"aria-current=""page""[^>]*>System</a>", page);
    }

    [Fact]
    public async Task A_failed_preview_is_listed_and_made_again()
    {
        await using var archive = await app.NewArchiveAsync();
        using var admin = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var fileId = await UploadAndWaitAsync(archive, admin, "torn-letter.jpg");
        var itemId = await archive.WithDbAsync(db => db.MediaFiles.Where(f => f.Id == fileId).Select(f => f.ArchiveItemId).SingleAsync());
        await FailAsync(archive, fileId);

        var listed = await admin.GetStringAsync("/Admin/System");
        var retried = await Browser.PostFormAsync(admin, $"/Admin/System?handler=Retry&id={fileId}", new(), "/Admin/System");
        await WaitUntilReadyAsync(archive, itemId);

        Assert.Contains("torn-letter.jpg", listed);
        Assert.Contains("Premature end of input file", listed);
        Assert.Contains("For one photo, no preview could be made.", listed);
        Assert.Contains("The preview is being made again.", await retried.Content.ReadAsStringAsync());
        Assert.Equal(1, await archive.WithDbAsync(db => db.AuditEntries.CountAsync(e => e.Action == AuditAction.PreviewRetried && e.ArchiveItemId == itemId)));
        Assert.Contains("No previews have failed.", await admin.GetStringAsync("/Admin/System"));
    }

    [Fact]
    public async Task All_failed_previews_can_be_tried_again_at_once()
    {
        await using var archive = await app.NewArchiveAsync();
        using var admin = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var first = await UploadAndWaitAsync(archive, admin, "one.jpg");
        var second = await UploadAndWaitAsync(archive, admin, "two.jpg");
        await FailAsync(archive, first);
        await FailAsync(archive, second);

        var retried = await Browser.PostFormAsync(admin, "/Admin/System?handler=Retry", new(), "/Admin/System");

        Assert.Contains("2 previews are being made again.", await retried.Content.ReadAsStringAsync());
        var statuses = await archive.WithDbAsync(db => db.MediaFiles.Select(f => f.Status).ToListAsync());
        Assert.DoesNotContain(ProcessingStatus.Failed, statuses);
    }

    [Fact]
    public async Task Retrying_a_preview_that_has_not_failed_finds_nothing()
    {
        await using var archive = await app.NewArchiveAsync();
        using var admin = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var fileId = await UploadAndWaitAsync(archive, admin, "fine.jpg");

        var response = await Browser.SendFormAsync(admin, $"/Admin/System?handler=Retry&id={fileId}", new(), "/Admin/System");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ProcessingStatus.Ready, await archive.WithDbAsync(db => db.MediaFiles.Where(f => f.Id == fileId).Select(f => f.Status).SingleAsync()));
    }
}
