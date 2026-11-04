using System.Net;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Commonage.Web.Operations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Commonage.Tests;

[Collection(nameof(AppCollection))]
public class ArchiveHealthTests(CommonageAppFactory app) : IDisposable
{
    private const long Megabyte = 1024 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "commonage-health-tests", Guid.NewGuid().ToString("N"));

    private string MediaFolder => Path.Combine(_root, "media");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeDisk(long? free) : IDiskSpace
    {
        public long? FreeBytes(string path) => free;
    }

    private ArchiveHealth Health(long? freeBytes, int minimumMegabytes = 100, int warnMegabytes = 500) => new(
        MediaFolder,
        new FakeDisk(freeBytes),
        Options.Create(new UploadOptions { MinimumFreeMegabytes = minimumMegabytes, WarnFreeMegabytes = warnMegabytes }),
        Options.Create(new BackupOptions { WarnAfterDays = 3 }),
        TimeProvider.System);

    /// <summary>The app with a pretend disk and a storage folder of its own, for its own backup date.</summary>
    private WebApplicationFactory<Program> AppWithDisk(long? freeBytes) =>
        app.WithWebHostBuilder(b => b
            .UseSetting("Storage:RootPath", MediaFolder)
            .UseSetting("Uploads:MinimumFreeMegabytes", "1024")
            .ConfigureTestServices(services => services.AddSingleton<IDiskSpace>(new FakeDisk(freeBytes))));

    private void RecordBackup(DateTime at)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "last-backup"), at.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\n");
    }

    private Task<string> CreateLinkAsync() => app.WithDbAsync(async db =>
    {
        var link = new ContributionLink
        {
            Title = TestData.Unique("Share your photos"),
            Token = Guid.NewGuid().ToString("N"),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            Collection = new Collection { Title = TestData.Unique("Contributions"), CreatedAt = DateTime.UtcNow },
        };
        db.ContributionLinks.Add(link);
        await db.SaveChangesAsync();
        return link.Token;
    });

    private static Dictionary<string, string> ContributionFields(string title) => new()
    {
        ["Input.Title"] = title,
        ["Input.Name"] = "Ada Contributor",
        ["Input.Rights"] = nameof(RightsStatement.CreatedByContributor),
        ["Input.Consent"] = "true",
    };

    private Task<int> CountAsync(string title) =>
        app.WithDbAsync(db => db.ArchiveItems.IgnoreQueryFilters().CountAsync(i => i.Title == title));

    [Theory]
    [InlineData(1000, 0, true)]
    [InlineData(100, 0, true)]
    [InlineData(99, 0, false)]
    [InlineData(1000, 450, true)]   // An upload counts twice: in the temporary folder, then in storage.
    [InlineData(1000, 451, false)]
    public void Uploads_are_refused_when_they_would_leave_too_little_free(int freeMegabytes, int uploadMegabytes, bool accepted)
    {
        Assert.Equal(accepted, Health(freeMegabytes * Megabyte).CanAcceptUploads(uploadMegabytes * Megabyte));
    }

    [Fact]
    public void When_free_space_is_unknown_uploads_stay_possible()
    {
        var health = Health(freeBytes: null);

        Assert.True(health.CanAcceptUploads(1000 * Megabyte));
        Assert.False(health.IsLowOnSpace);
    }

    [Fact]
    public void The_real_disk_reports_free_space_even_for_a_folder_that_does_not_exist_yet()
    {
        var free = new SystemDiskSpace(Microsoft.Extensions.Logging.Abstractions.NullLogger<SystemDiskSpace>.Instance)
            .FreeBytes(Path.Combine(MediaFolder, "not", "yet"));

        Assert.True(free > 0);
    }

    [Fact]
    public void A_recent_backup_is_not_overdue_but_an_old_one_is_and_no_backup_is_too()
    {
        Assert.True(Health(null).BackupIsOverdue);
        Assert.Null(Health(null).DaysSinceLastBackup);

        RecordBackup(DateTime.UtcNow.AddDays(-1));
        Assert.False(Health(null).BackupIsOverdue);

        RecordBackup(DateTime.UtcNow.AddDays(-5));
        Assert.True(Health(null).BackupIsOverdue);
        Assert.Equal(5, Health(null).DaysSinceLastBackup);
    }

    [Fact]
    public void An_unreadable_backup_date_counts_as_no_backup()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "last-backup"), "not a date");

        Assert.Null(Health(null).LastBackupAt);
    }

    [Fact]
    public async Task With_a_nearly_full_disk_a_contribution_is_refused_and_nothing_is_stored()
    {
        var token = await CreateLinkAsync();
        await using var full = AppWithDisk(10 * Megabyte);
        using var visitor = full.CreateClient();
        var title = TestData.Unique("Refused");

        var form = await visitor.GetStringAsync($"/contribute/{token}");
        // Sent as if the form had been open since before the disk filled up. The refusal comes before anything is read.
        var content = new MultipartFormDataContent();
        foreach (var (name, value) in ContributionFields(title))
            content.Add(new StringContent(value), name);
        content.Add(new ByteArrayContent(TestData.Photo), "Files", "flood.jpg");
        var response = await visitor.PostAsync($"/contribute/{token}", content);

        Assert.Contains("storage is full", form);
        Assert.DoesNotContain("enctype=\"multipart/form-data\"", form);
        Assert.Equal(HttpStatusCode.InsufficientStorage, response.StatusCode);
        Assert.Contains("was not saved", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, await CountAsync(title));
    }

    [Fact]
    public async Task With_a_nearly_full_disk_curators_cannot_upload_but_can_still_save_text()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Draft);
        var page = $"/Admin/Items/Edit/{item.Id}";
        await using var full = AppWithDisk(10 * Megabyte);
        using var curator = full.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);

        var html = await curator.GetStringAsync(page);
        await Browser.PostFormAsync(curator, page, new()
        {
            ["Input.Title"] = item.Title,
            ["Input.CollectionId"] = item.CollectionId.ToString(),
            ["Input.Description"] = "Text still saves",
        });

        Assert.Contains("The disk is nearly full", html);
        Assert.DoesNotContain("photo-upload", html);
        Assert.Equal("Text still saves", await app.WithDbAsync(db => db.ArchiveItems.Where(i => i.Id == item.Id).Select(i => i.Description).SingleAsync()));
    }

    [Fact]
    public async Task With_enough_room_uploads_work_as_usual()
    {
        var item = await app.CreateContributionAsync(ItemStatus.Draft);
        await using var roomy = AppWithDisk(100_000 * Megabyte);
        using var curator = roomy.CreateClient();
        await Browser.LogInAsCuratorAsync(curator);

        await Browser.UploadAsync(curator, $"/Admin/Items/Edit/{item.Id}", "Upload", "flood.jpg", TestData.Photo);

        Assert.True(await app.WithDbAsync(db => db.MediaFiles.AnyAsync(f => f.ArchiveItemId == item.Id)));
    }

    [Fact]
    public async Task A_link_that_reached_its_daily_limit_refuses_photos_but_takes_text()
    {
        var token = await CreateLinkAsync();
        await using var strict = app.WithWebHostBuilder(b => b.UseSetting("Contributions:MaxUploadMegabytesPerLinkPerDay", "0"));
        using var visitor = strict.CreateClient();
        var withPhoto = TestData.Unique("With photo");
        var withoutPhoto = TestData.Unique("Without photo");

        var refused = await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", ContributionFields(withPhoto), ("flood.jpg", TestData.Photo));
        await Browser.SendFormWithFilesAsync(visitor, $"/contribute/{token}", ContributionFields(withoutPhoto));

        Assert.Contains("A lot has been sent through this link today", await refused.Content.ReadAsStringAsync());
        Assert.Equal(0, await CountAsync(withPhoto));
        Assert.Equal(1, await CountAsync(withoutPhoto));
    }

    [Fact]
    public async Task Admins_are_told_about_a_missing_backup_and_low_disk_space_but_curators_are_not()
    {
        await using var low = AppWithDisk(300 * Megabyte);
        using var admin = low.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);
        var (_, _, email, password) = await app.CreateAccountAsync(admin);
        using var curator = low.CreateClient();
        await Browser.LogInAsync(curator, email, password);

        var adminPage = await admin.GetStringAsync("/Admin");
        var curatorPage = await curator.GetStringAsync("/Admin");

        Assert.Contains("No backup is recorded", adminPage);
        Assert.Contains("The disk is nearly full", adminPage);
        Assert.DoesNotContain("No backup is recorded", curatorPage);
        Assert.DoesNotContain("The disk is nearly full", curatorPage);
    }

    [Fact]
    public async Task A_recent_backup_is_shown_quietly()
    {
        RecordBackup(DateTime.UtcNow.AddHours(-2));
        await using var healthy = AppWithDisk(100_000 * Megabyte);
        using var admin = healthy.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);

        var page = await admin.GetStringAsync("/Admin");

        Assert.Contains("Last backup:", page);
        Assert.DoesNotContain("No backup is recorded", page);
    }

    [Fact]
    public async Task Too_many_login_attempts_from_one_address_get_a_friendly_refusal()
    {
        await using var strict = app.WithWebHostBuilder(b => b.UseSetting("Login:MaxAttemptsPerHour", "2"));
        using var visitor = strict.CreateClient();
        var attempt = new Dictionary<string, string>
        {
            ["Input.Email"] = "nobody@example.org",
            ["Input.Password"] = "not the password",
        };

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage last = null!;
        for (var i = 0; i < 3; i++)
        {
            last = await Browser.SendFormAsync(visitor, "/Account/Login", new(attempt));
            statuses.Add(last.StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        Assert.Contains("Too many attempts", await last.Content.ReadAsStringAsync());
    }
}
