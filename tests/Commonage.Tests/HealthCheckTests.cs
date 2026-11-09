using System.Net;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Commonage.Web.Operations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

/// <summary>
/// /health is what update.sh and uptime monitors look at. Each test has an archive of its own, with its own media folder,
/// because the newest original and the last backup are checked.
/// </summary>
[Collection(nameof(AppCollection))]
public class HealthCheckTests(CommonageAppFactory app) : IDisposable
{
    private const long Megabyte = 1024 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "commonage-health-checks", Guid.NewGuid().ToString("N"));

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

    private sealed class ReadOnlyStorage(IFileStorage real) : IFileStorage
    {
        public Task SaveAsync(string key, Stream content, CancellationToken cancellationToken = default) =>
            throw new UnauthorizedAccessException("Read-only file system");

        public Stream OpenRead(string key) => real.OpenRead(key);

        public void Delete(string key) => real.Delete(key);
    }

    private Task<WebApplicationFactory<Program>> ArchiveAsync(long? freeBytes = 100_000 * Megabyte, bool readOnly = false) =>
        app.NewArchiveAsync(b => b
            .UseSetting("Storage:RootPath", MediaFolder)
            .UseSetting("Uploads:MinimumFreeMegabytes", "100")
            .UseSetting("Uploads:WarnFreeMegabytes", "500")
            .ConfigureTestServices(services =>
            {
                services.AddSingleton<IDiskSpace>(new FakeDisk(freeBytes));
                if (readOnly)
                    services.AddSingleton<IFileStorage>(new ReadOnlyStorage(new LocalFileStorage(MediaFolder)));
            }));

    private void RecordBackup(DateTime at)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "last-backup"), at.ToString("yyyy-MM-ddTHH:mm:ssZ") + "\n");
    }

    private static async Task<(HttpStatusCode Status, string Body)> HealthAsync(WebApplicationFactory<Program> archive)
    {
        using var client = archive.CreateClient();
        var response = await client.GetAsync("/health");
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>A photo whose original is stored, as an upload leaves it.</summary>
    private static Task<MediaFile> AddPhotoAsync(WebApplicationFactory<Program> archive, bool withOriginal = true) => archive.WithDbAsync(async db =>
    {
        var file = new MediaFile
        {
            OriginalFileName = "mill.jpg",
            ContentType = "image/jpeg",
            Sha256 = new string('0', 64),
            StorageId = Guid.NewGuid(),
            UploadedAt = DateTime.UtcNow,
            ArchiveItem = new ArchiveItem
            {
                Title = TestData.Unique("Mill"),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Collection = new Collection { Title = TestData.Unique("Photos"), CreatedAt = DateTime.UtcNow },
            },
        };
        if (withOriginal)
            await archive.Services.GetRequiredService<IFileStorage>().SaveAsync(file.OriginalKey, new MemoryStream(TestData.Photo));
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync();
        return file;
    });

    [Fact]
    public async Task With_a_recent_backup_and_its_files_in_place_the_archive_is_healthy()
    {
        await using var archive = await ArchiveAsync();
        RecordBackup(DateTime.UtcNow.AddHours(-3));
        await AddPhotoAsync(archive);

        Assert.Equal((HttpStatusCode.OK, "Healthy"), await HealthAsync(archive));
        Assert.Empty(Directory.GetFiles(MediaFolder, "health-check*"));
    }

    [Fact]
    public async Task Without_a_backup_or_with_an_old_one_it_needs_attention_and_still_answers_200()
    {
        await using var archive = await ArchiveAsync();

        var none = await HealthAsync(archive);
        RecordBackup(DateTime.UtcNow.AddDays(-5));
        var old = await HealthAsync(archive);

        Assert.Equal((HttpStatusCode.OK, "Degraded"), none);
        Assert.Equal((HttpStatusCode.OK, "Degraded"), old);
    }

    [Theory]
    [InlineData(300)]   // Below the warning, uploads still work.
    [InlineData(10)]    // Uploads have stopped.
    public async Task A_disk_that_is_filling_up_needs_attention(int freeMegabytes)
    {
        await using var archive = await ArchiveAsync(freeMegabytes * Megabyte);
        RecordBackup(DateTime.UtcNow);

        Assert.Equal((HttpStatusCode.OK, "Degraded"), await HealthAsync(archive));
    }

    [Fact]
    public async Task A_media_folder_that_cannot_be_written_to_is_unhealthy()
    {
        await using var archive = await ArchiveAsync(readOnly: true);
        RecordBackup(DateTime.UtcNow);

        Assert.Equal((HttpStatusCode.ServiceUnavailable, "Unhealthy"), await HealthAsync(archive));
    }

    /// <summary>What a media volume that is not mounted, or a database restored without its files, looks like.</summary>
    [Fact]
    public async Task A_missing_newest_original_is_unhealthy()
    {
        await using var archive = await ArchiveAsync();
        RecordBackup(DateTime.UtcNow);
        await AddPhotoAsync(archive);
        await AddPhotoAsync(archive, withOriginal: false);

        Assert.Equal((HttpStatusCode.ServiceUnavailable, "Unhealthy"), await HealthAsync(archive));
    }

    [Fact]
    public async Task The_answer_names_no_detail()
    {
        await using var archive = await ArchiveAsync(10 * Megabyte, readOnly: true);

        var (_, body) = await HealthAsync(archive);

        Assert.Equal("Unhealthy", body);
    }
}
