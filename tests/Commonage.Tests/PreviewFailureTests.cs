using System.Collections.Concurrent;
using System.Net;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Commonage.Tests;

/// <summary>
/// Anyone with a contribution link can send in a file, so a file that the preview maker cannot cope with must cost the
/// archive that one preview, and nothing else.
/// </summary>
[Collection(nameof(AppCollection))]
public class PreviewFailureTests(CommonageAppFactory app) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "commonage-preview-failures", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>Fails in a way the image library never reports, for the originals it is told to, and remembers what was opened.</summary>
    private sealed class WatchedStorage(IFileStorage real) : IFileStorage
    {
        public ConcurrentDictionary<string, bool> Broken { get; } = new();

        public ConcurrentQueue<string> Opened { get; } = new();

        public Task SaveAsync(string key, Stream content, CancellationToken cancellationToken = default) => real.SaveAsync(key, content, cancellationToken);

        public Stream OpenRead(string key)
        {
            Opened.Enqueue(key);
            return Broken.ContainsKey(key) ? throw new InvalidOperationException("Not something the image library reports.") : real.OpenRead(key);
        }

        public void Delete(string key) => real.Delete(key);
    }

    private async Task<(WebApplicationFactory<Program> Archive, WatchedStorage Storage)> ArchiveAsync()
    {
        var storage = new WatchedStorage(new LocalFileStorage(_root));
        var archive = await app.NewArchiveAsync(b => b
            .UseSetting("Storage:RootPath", _root)
            .ConfigureTestServices(services => services.AddSingleton<IFileStorage>(storage)));
        return (archive, storage);
    }

    /// <summary>A photo as an upload leaves it: stored, and waiting for its previews.</summary>
    private static async Task<MediaFile> UploadAsync(WebApplicationFactory<Program> archive, WatchedStorage storage, bool broken = false, int attempts = 0)
    {
        var file = await archive.WithDbAsync(async db =>
        {
            var file = new MediaFile
            {
                OriginalFileName = "mill.jpg",
                ContentType = "image/jpeg",
                Sha256 = new string('0', 64),
                StorageId = Guid.NewGuid(),
                UploadedAt = DateTime.UtcNow,
                ProcessingAttempts = attempts,
                ArchiveItem = new ArchiveItem
                {
                    Title = TestData.Unique("Mill"),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Collection = new Collection { Title = TestData.Unique("Photos"), CreatedAt = DateTime.UtcNow },
                },
            };
            await storage.SaveAsync(file.OriginalKey, new MemoryStream(TestData.Photo));
            if (broken)
                storage.Broken[file.OriginalKey] = true;
            db.MediaFiles.Add(file);
            await db.SaveChangesAsync();
            return file;
        });
        archive.Services.GetRequiredService<MediaProcessingQueue>().Enqueue(file.Id);
        return file;
    }

    private static async Task<MediaFile> WhenDoneAsync(WebApplicationFactory<Program> archive, MediaFile file)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var now = await archive.WithDbAsync(db => db.MediaFiles.FindAsync(file.Id).AsTask());
            if (now!.Status != ProcessingStatus.Pending)
                return now;
            Assert.True(DateTime.UtcNow < deadline, "The preview maker did not get to the file in time.");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task A_file_the_preview_maker_cannot_cope_with_fails_on_its_own()
    {
        var (archive, storage) = await ArchiveAsync();
        await using var _ = archive;

        var bad = await WhenDoneAsync(archive, await UploadAsync(archive, storage, broken: true));
        var next = await WhenDoneAsync(archive, await UploadAsync(archive, storage));
        using var visitor = archive.CreateClient();
        var startPage = await visitor.GetAsync("/");

        Assert.Equal(ProcessingStatus.Failed, bad.Status);
        Assert.Equal(ProcessingStatus.Ready, next.Status);
        Assert.Equal(HttpStatusCode.OK, startPage.StatusCode);
    }

    /// <summary>
    /// What is left of a file that took the app down each time: it is still waiting, and the count of attempts is all there
    /// is to go by. Opening it once more would take the app down once more, at every start.
    /// </summary>
    [Fact]
    public async Task A_file_the_archive_stopped_on_every_time_is_given_up_without_opening_it_again()
    {
        var (archive, storage) = await ArchiveAsync();
        await using var _ = archive;

        var file = await WhenDoneAsync(archive, await UploadAsync(archive, storage, attempts: MediaFile.MaxProcessingAttempts));

        Assert.Equal(ProcessingStatus.Failed, file.Status);
        Assert.DoesNotContain(file.OriginalKey, storage.Opened);
    }

    [Fact]
    public async Task Each_attempt_is_counted_and_trying_again_by_hand_starts_the_count_anew()
    {
        var (archive, storage) = await ArchiveAsync();
        await using var _ = archive;
        var file = await WhenDoneAsync(archive, await UploadAsync(archive, storage, broken: true, attempts: MediaFile.MaxProcessingAttempts - 1));
        using var admin = archive.CreateClient();
        await Browser.LogInAsCuratorAsync(admin);

        await Browser.PostFormAsync(admin, "/Admin/System?handler=Retry", new() { ["id"] = file.Id.ToString() }, pageUrl: "/Admin/System");
        var again = await WhenDoneAsync(archive, file);

        Assert.Equal(MediaFile.MaxProcessingAttempts, file.ProcessingAttempts);
        Assert.Equal(1, again.ProcessingAttempts);
    }
}
