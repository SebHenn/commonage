using System.Security.Cryptography;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Commonage.Web.Media;

public class UploadOptions
{
    public int MaxFileSizeMegabytes { get; set; } = 200;

    public long MaxFileSizeBytes => MaxFileSizeMegabytes * 1024L * 1024L;

    /// <summary>
    /// Images with more pixels get no preview. A small file can claim to be a huge image (a "decompression bomb");
    /// this keeps such a file from tying up the server. 250 megapixels is more than a large-format scan at 600 dpi.
    /// </summary>
    public int MaxPreviewMegapixels { get; set; } = 250;
}

public enum UploadError
{
    None,
    Empty,
    TooLarge,
    UnsupportedFormat,
}

public static class UploadErrorMessages
{
    public static string Describe(IStringLocalizer localizer, UploadError error, string fileName) => error switch
    {
        UploadError.Empty => localizer["{0} is empty.", fileName],
        UploadError.TooLarge => localizer["{0} is too large.", fileName],
        _ => localizer["{0} is not a supported photo format. Please use JPEG, PNG, TIFF or WebP.", fileName],
    };
}

/// <summary>Validates an uploaded file, stores the original unchanged and queues preview generation.</summary>
public class MediaUploadService(
    CommonageDbContext db,
    IFileStorage storage,
    MediaProcessingQueue queue,
    IOptions<UploadOptions> options)
{
    /// <summary>Checks a file without storing it, so a form can reject it before saving anything else.</summary>
    public async Task<UploadError> CheckAsync(IFormFile upload, CancellationToken cancellationToken = default)
    {
        await using var content = upload.OpenReadStream();
        return (await DetectAsync(upload, content, cancellationToken)).Error;
    }

    public async Task<UploadError> AddAsync(ArchiveItem item, IFormFile upload, CancellationToken cancellationToken = default)
    {
        await using var content = upload.OpenReadStream();
        var (error, contentType) = await DetectAsync(upload, content, cancellationToken);
        if (error != UploadError.None)
            return error;

        content.Position = 0;
        var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken));
        content.Position = 0;

        var file = new MediaFile
        {
            ArchiveItem = item,
            OriginalFileName = CleanFileName(upload.FileName),
            ContentType = contentType!,
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

    private async Task<(UploadError Error, string? ContentType)> DetectAsync(IFormFile upload, Stream content, CancellationToken cancellationToken)
    {
        if (upload.Length == 0)
            return (UploadError.Empty, null);
        if (upload.Length > options.Value.MaxFileSizeBytes)
            return (UploadError.TooLarge, null);

        var header = new byte[ImageFormats.HeaderLength];
        var headerLength = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        var contentType = ImageFormats.DetectContentType(header.AsSpan(0, headerLength));
        return contentType is null ? (UploadError.UnsupportedFormat, null) : (UploadError.None, contentType);
    }

    private static string CleanFileName(string fileName)
    {
        var name = new string(Path.GetFileName(fileName).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0)
            return "upload";
        return name.Length <= 255 ? name : name[..255];
    }
}
