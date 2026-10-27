using System.Security.Cryptography;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.Extensions.Options;

namespace Commonage.Web.Media;

public class UploadOptions
{
    public int MaxFileSizeMegabytes { get; set; } = 200;

    public long MaxFileSizeBytes => MaxFileSizeMegabytes * 1024L * 1024L;
}

public enum UploadError
{
    None,
    Empty,
    TooLarge,
    UnsupportedFormat,
}

/// <summary>Validates an uploaded file, stores the original unchanged and queues preview generation.</summary>
public class MediaUploadService(
    CommonageDbContext db,
    IFileStorage storage,
    MediaProcessingQueue queue,
    IOptions<UploadOptions> options)
{
    public async Task<UploadError> AddAsync(ArchiveItem item, IFormFile upload, CancellationToken cancellationToken = default)
    {
        if (upload.Length == 0)
            return UploadError.Empty;
        if (upload.Length > options.Value.MaxFileSizeBytes)
            return UploadError.TooLarge;

        await using var content = upload.OpenReadStream();
        var header = new byte[ImageFormats.HeaderLength];
        var headerLength = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        var contentType = ImageFormats.DetectContentType(header.AsSpan(0, headerLength));
        if (contentType is null)
            return UploadError.UnsupportedFormat;

        content.Position = 0;
        var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken));
        content.Position = 0;

        var file = new MediaFile
        {
            ArchiveItem = item,
            OriginalFileName = CleanFileName(upload.FileName),
            ContentType = contentType,
            SizeBytes = upload.Length,
            Sha256 = sha256,
            StorageId = Guid.NewGuid(),
            Status = ProcessingStatus.Pending,
            UploadedAt = DateTime.UtcNow,
        };
        await storage.SaveAsync(file.OriginalKey, content, cancellationToken);
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync(cancellationToken);

        queue.Enqueue(file.Id);
        return UploadError.None;
    }

    public async Task DeleteAsync(MediaFile file, CancellationToken cancellationToken = default)
    {
        db.MediaFiles.Remove(file);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var key in new[] { file.OriginalKey, file.PreviewKey, file.ThumbnailKey })
            storage.Delete(key);
    }

    private static string CleanFileName(string fileName)
    {
        var name = new string(Path.GetFileName(fileName).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0)
            return "upload";
        return name.Length <= 255 ? name : name[..255];
    }
}
