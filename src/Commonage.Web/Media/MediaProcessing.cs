using System.Threading.Channels;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NetVips;

namespace Commonage.Web.Media;

/// <summary>Ids of media files waiting for preview generation.</summary>
public class MediaProcessingQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();

    public void Enqueue(int mediaFileId) => _channel.Writer.TryWrite(mediaFileId);

    private int _lookAgain;

    /// <summary>Something changed about which sizes are wanted, so older files have to be looked at once more.</summary>
    public void LookThroughOlderFilesAgain()
    {
        Interlocked.Exchange(ref _lookAgain, 1);
        // Wakes the processor if it is waiting. It ignores an id that does not exist.
        _channel.Writer.TryWrite(0);
    }

    public bool ShouldLookAgain() => Interlocked.Exchange(ref _lookAgain, 0) == 1;

    public bool TryDequeue(out int mediaFileId) => _channel.Reader.TryRead(out mediaFileId);

    public ValueTask<bool> WaitAsync(CancellationToken cancellationToken) => _channel.Reader.WaitToReadAsync(cancellationToken);
}

/// <summary>
/// Creates a preview and a thumbnail for each uploaded image, one file at a time so a small server
/// is never overloaded. Previews are rotated upright and carry no metadata: EXIF data such as GPS
/// coordinates and camera serial numbers stays in the private original only.
/// </summary>
public class MediaProcessor(
    MediaProcessingQueue queue,
    IServiceScopeFactory scopeFactory,
    IFileStorage storage,
    IOptions<UploadOptions> options,
    ILogger<MediaProcessor> logger) : BackgroundService
{
    /// <summary>About as large as a scan needs to be for reading handwriting, and far from the full original.</summary>
    private const int LargeSize = 3000;
    private const int PreviewSize = 1600;
    private const int ThumbnailSize = 400;
    private const int CardSize = 800;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueUnfinishedAsync(stoppingToken);

        // New uploads first. Older files get new picture sizes whenever nothing else is waiting, so an archive with
        // many photos stays usable meanwhile. The last id remembers where this pass got to, so a file that cannot be
        // upgraded is tried again after the next start, not over and over.
        var lastUpgraded = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (queue.ShouldLookAgain())
                lastUpgraded = 0;

            if (queue.TryDequeue(out var mediaFileId))
            {
                await ProcessAsync(mediaFileId, stoppingToken);
            }
            else if (await NextToUpgradeAsync(lastUpgraded, stoppingToken) is { } upgrade)
            {
                lastUpgraded = upgrade;
                await UpgradeAsync(upgrade, stoppingToken);
            }
            else
            {
                await queue.WaitAsync(stoppingToken);
            }
        }
    }

    private async Task<int?> NextToUpgradeAsync(int after, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
        var large = await LargeIsWantedAsync(db, cancellationToken);
        return await db.MediaFiles
            .IgnoreQueryFilters()
            .Where(f => f.Id > after && f.Status == ProcessingStatus.Ready
                && (f.DerivativeVersion < MediaFile.CurrentDerivativeVersion || f.Rotation != f.RenderedRotation
                    || (large && !f.HasLarge && (f.PreviewWidth >= PreviewSize || f.PreviewHeight >= PreviewSize))))
            .OrderBy(f => f.Id)
            .Select(f => (int?)f.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Adds the sizes a file does not have yet. The file stays Ready and keeps being served throughout, because setting it
    /// back to Pending would make a published photo disappear until its turn came.
    /// </summary>
    private async Task UpgradeAsync(int mediaFileId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
        var file = await db.MediaFiles.IgnoreQueryFilters().FirstOrDefaultAsync(f => f.Id == mediaFileId, cancellationToken);
        if (file is null)
            return;

        var large = await LargeIsWantedAsync(db, cancellationToken);
        try
        {
            if (file.IsBeingTurned || file.DerivativeVersion < 2)
            {
                await RenderAllAsync(file, large, cancellationToken);
            }
            else if (large && !file.HasLarge && (file.PreviewWidth >= PreviewSize || file.PreviewHeight >= PreviewSize))
            {
                // Only the large size is missing, so the sizes people are looking at are left alone.
                await using var original = storage.OpenRead(file.OriginalKey);
                var picture = Resize(original, LargeSize, file.Rotation, out var width, out var height);
                if (Math.Max(width, height) > PreviewSize)
                {
                    await storage.SaveAsync(file.LargeKey, new MemoryStream(picture), cancellationToken);
                    file.HasLarge = true;
                }
            }

            file.DerivativeVersion = MediaFile.CurrentDerivativeVersion;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is VipsException or IOException)
        {
            // It keeps its old sizes, which is all a visitor would notice. A turn that cannot be done is given up, so that nobody waits for it.
            logger.LogWarning(exception, "Could not add new picture sizes for media file {MediaFileId}", mediaFileId);
            if (file.IsBeingTurned)
            {
                file.Rotation = file.RenderedRotation;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            DeleteDerivatives(file);
        }
    }

    /// <summary>Without the large size, an archive that does not use it saves the disk space and the work.</summary>
    private static async Task<bool> LargeIsWantedAsync(CommonageDbContext db, CancellationToken cancellationToken) =>
        (await db.ArchiveSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken) ?? new ArchiveSettings()).LargePhotos;

    private void DeleteDerivatives(MediaFile file)
    {
        foreach (var key in new[] { file.PreviewKey, file.ThumbnailKey, file.CardKey, file.LargeKey })
            storage.Delete(key);
    }

    /// <summary>
    /// Makes every size from the original, turned as curators asked. Each is replaced at once when it is ready, so the
    /// file is served without a gap. The largest comes first and the others from it, which is far less work for a big scan.
    /// </summary>
    private async Task RenderAllAsync(MediaFile file, bool large, CancellationToken cancellationToken)
    {
        var turnedTo = file.Rotation;
        byte[]? largePicture = null;
        if (large)
        {
            await using var original = storage.OpenRead(file.OriginalKey);
            largePicture = Resize(original, LargeSize, turnedTo, out var width, out var height);
            if (Math.Max(width, height) <= PreviewSize)
                largePicture = null;
        }

        byte[] preview;
        int previewWidth, previewHeight;
        if (largePicture is not null)
        {
            preview = Resize(new MemoryStream(largePicture), PreviewSize, 0, out previewWidth, out previewHeight);
        }
        else
        {
            await using var original = storage.OpenRead(file.OriginalKey);
            preview = Resize(original, PreviewSize, turnedTo, out previewWidth, out previewHeight);
        }

        var thumbnail = Resize(new MemoryStream(preview), ThumbnailSize, 0, out _, out _);
        var card = Resize(new MemoryStream(preview), CardSize, 0, out _, out _);

        if (largePicture is not null)
            await storage.SaveAsync(file.LargeKey, new MemoryStream(largePicture), cancellationToken);
        else
            storage.Delete(file.LargeKey);
        await storage.SaveAsync(file.PreviewKey, new MemoryStream(preview), cancellationToken);
        await storage.SaveAsync(file.ThumbnailKey, new MemoryStream(thumbnail), cancellationToken);
        await storage.SaveAsync(file.CardKey, new MemoryStream(card), cancellationToken);

        file.HasLarge = largePicture is not null;
        file.PreviewWidth = previewWidth;
        file.PreviewHeight = previewHeight;
        file.RenderedRotation = turnedTo;
        file.DerivativeVersion = MediaFile.CurrentDerivativeVersion;
    }

    /// <summary>Picks up files that were still waiting when the app last stopped.</summary>
    private async Task RequeueUnfinishedAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
        // Photos in the bin included: they may come back, and should then have their previews.
        var pending = await db.MediaFiles
            .IgnoreQueryFilters()
            .Where(f => f.Status == ProcessingStatus.Pending)
            .Select(f => f.Id)
            .ToListAsync(cancellationToken);
        pending.ForEach(queue.Enqueue);
    }

    private async Task ProcessAsync(int mediaFileId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CommonageDbContext>();
        var file = await db.MediaFiles.IgnoreQueryFilters().FirstOrDefaultAsync(f => f.Id == mediaFileId, cancellationToken);
        if (file is { Status: ProcessingStatus.Ready, IsBeingTurned: true })
        {
            await UpgradeAsync(mediaFileId, cancellationToken);
            return;
        }
        if (file is null || file.Status != ProcessingStatus.Pending)
            return;

        try
        {
            await using (var original = storage.OpenRead(file.OriginalKey))
            {
                // Only the header is read here; libvips decodes pixels when they are needed.
                using var header = Image.NewFromStream(original, access: Enums.Access.Sequential);
                if ((long)header.Width * header.Height > options.Value.MaxPreviewMegapixels * 1_000_000L)
                    throw new VipsException($"The image has {header.Width} × {header.Height} pixels, more than the limit of {options.Value.MaxPreviewMegapixels} megapixels.");
            }

            await RenderAllAsync(file, await LargeIsWantedAsync(db, cancellationToken), cancellationToken);
            file.Status = ProcessingStatus.Ready;
            file.ProcessingError = null;
        }
        catch (Exception exception) when (exception is VipsException or IOException)
        {
            logger.LogWarning(exception, "Could not create previews for media file {MediaFileId}", file.Id);
            file.Status = ProcessingStatus.Failed;
            file.ProcessingError = exception.Message.Length <= 1000 ? exception.Message : exception.Message[..1000];
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A curator deleted the file, or its whole contribution, while the previews were being made.
            // An exception here would stop the app, so the new previews are removed instead.
            DeleteDerivatives(file);
        }
    }

    /// <summary>Shrinks the image to fit a square of the given size, applying the EXIF rotation and then the given one, and drops all metadata.</summary>
    private static byte[] Resize(Stream source, int size, int rotation, out int width, out int height)
    {
        using var shrunk = Image.ThumbnailStream(source, size, height: size, size: Enums.Size.Down);
        // Turning reads the picture out of order, which a picture coming straight from a file cannot do. It is small by now.
        using var inMemory = rotation == 0 ? null : shrunk.CopyMemory();
        using var turned = rotation switch
        {
            90 => inMemory!.Rot90(),
            180 => inMemory!.Rot180(),
            270 => inMemory!.Rot270(),
            _ => null,
        };
        var image = turned ?? shrunk;
        width = image.Width;
        height = image.Height;
        using var output = new MemoryStream();
        image.JpegsaveStream(output, q: 85, keep: Enums.ForeignKeep.None);
        return output.ToArray();
    }
}
