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

    public IAsyncEnumerable<int> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
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
    private const int PreviewSize = 1600;
    private const int ThumbnailSize = 400;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueUnfinishedAsync(stoppingToken);

        await foreach (var mediaFileId in queue.ReadAllAsync(stoppingToken))
        {
            await ProcessAsync(mediaFileId, stoppingToken);
        }
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

            byte[] preview;
            await using (var original = storage.OpenRead(file.OriginalKey))
            {
                preview = Resize(original, PreviewSize, out var width, out var height);
                file.PreviewWidth = width;
                file.PreviewHeight = height;
            }

            using var previewStream = new MemoryStream(preview);
            var thumbnail = Resize(previewStream, ThumbnailSize, out _, out _);

            await storage.SaveAsync(file.PreviewKey, new MemoryStream(preview), cancellationToken);
            await storage.SaveAsync(file.ThumbnailKey, new MemoryStream(thumbnail), cancellationToken);
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
            storage.Delete(file.PreviewKey);
            storage.Delete(file.ThumbnailKey);
        }
    }

    /// <summary>Shrinks the image to fit a square of the given size, applying the EXIF rotation, and drops all metadata.</summary>
    private static byte[] Resize(Stream source, int size, out int width, out int height)
    {
        using var image = Image.ThumbnailStream(source, size, height: size, size: Enums.Size.Down);
        width = image.Width;
        height = image.Height;
        using var output = new MemoryStream();
        image.JpegsaveStream(output, q: 85, keep: Enums.ForeignKeep.None);
        return output.ToArray();
    }
}
