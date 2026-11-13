using System.Security.Cryptography;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>Uploads are refused when less than this is free, because a full disk also stops the database.</summary>
    public int MinimumFreeMegabytes { get; set; } = 1024;

    /// <summary>Admins get a notice when less than this is free.</summary>
    public int WarnFreeMegabytes { get; set; } = 5120;
}

public enum UploadError
{
    None,
    Empty,
    TooLarge,
    UnsupportedFormat,
    Heic,
}

/// <summary>What a look at an uploaded file found. Its fingerprint is only there when the file is acceptable.</summary>
public record ExaminedUpload(UploadError Error, string? ContentType = null, string? Sha256 = null);

/// <summary>An item that already holds a file with the same content.</summary>
public record IdenticalFile(int ItemId, string ItemTitle);

public static class UploadErrorMessages
{
    public static string Describe(IStringLocalizer localizer, UploadError error, string fileName) => error switch
    {
        UploadError.Empty => localizer["{0} is empty.", fileName],
        UploadError.TooLarge => localizer["{0} is too large.", fileName],
        UploadError.Heic => localizer["{0} is in the HEIC format, which this archive cannot read yet. Please save it as a JPEG and choose it again.", fileName],
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
        return (await DetectAsync(upload.Length, content, cancellationToken)).Error;
    }

    public async Task<UploadError> AddAsync(ArchiveItem item, IFormFile upload, CancellationToken cancellationToken = default)
    {
        var examined = await ExamineAsync(upload, cancellationToken);
        if (examined.Error == UploadError.None)
            await StoreAsync(item, upload, examined, cancellationToken);
        return examined.Error;
    }

    /// <summary>Checks a file and takes its fingerprint, to see whether the archive already has it before anything is stored.</summary>
    public async Task<ExaminedUpload> ExamineAsync(IFormFile upload, CancellationToken cancellationToken = default)
    {
        await using var content = upload.OpenReadStream();
        return await ExamineAsync(content, upload.Length, cancellationToken);
    }

    /// <summary>The same for a file that is not an upload, such as one from an import. The stream must be seekable.</summary>
    public async Task<ExaminedUpload> ExamineAsync(Stream content, long length, CancellationToken cancellationToken = default)
    {
        content.Position = 0;
        var (error, contentType) = await DetectAsync(length, content, cancellationToken);
        if (error != UploadError.None)
            return new ExaminedUpload(error);

        content.Position = 0;
        return new ExaminedUpload(UploadError.None, contentType, Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken)));
    }

    /// <summary>An item with the same file, if there is one. What is in the bin does not count: someone chose to throw that away.</summary>
    public async Task<IdenticalFile?> FindIdenticalAsync(ExaminedUpload examined, CancellationToken cancellationToken = default) =>
        await db.MediaFiles
            .Where(f => f.Sha256 == examined.Sha256)
            .OrderBy(f => f.Id)
            .Select(f => new IdenticalFile(f.ArchiveItemId, f.ArchiveItem.Title))
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>Stores a file that passed <see cref="ExamineAsync"/>. Also saves whatever else is waiting in the context, such as a new item.</summary>
    public async Task<MediaFile> StoreAsync(ArchiveItem item, IFormFile upload, ExaminedUpload examined, CancellationToken cancellationToken = default)
    {
        await using var content = upload.OpenReadStream();
        return await StoreAsync(item, content, upload.FileName, upload.Length, examined, cancellationToken);
    }

    /// <summary>The same for a file that is not an upload, such as one from an import.</summary>
    public async Task<MediaFile> StoreAsync(ArchiveItem item, Stream content, string fileName, long length, ExaminedUpload examined, CancellationToken cancellationToken = default)
    {
        // After everything else, also what is in the bin, because it may come back to its place.
        var last = await db.MediaFiles.IgnoreQueryFilters().Where(f => f.ArchiveItemId == item.Id).MaxAsync(f => (int?)f.Position, cancellationToken);
        var file = new MediaFile
        {
            ArchiveItem = item,
            Position = (last ?? -1) + 1,
            OriginalFileName = CleanFileName(fileName),
            ContentType = examined.ContentType!,
            SizeBytes = length,
            Sha256 = examined.Sha256!,
            StorageId = Guid.NewGuid(),
            Status = ProcessingStatus.Pending,
            UploadedAt = DateTime.UtcNow,
        };
        content.Position = 0;
        await storage.SaveAsync(file.OriginalKey, content, cancellationToken);
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync(cancellationToken);

        queue.Enqueue(file.Id);
        return file;
    }

    public async Task DeleteAsync(MediaFile file, CancellationToken cancellationToken = default)
    {
        db.MediaFiles.Remove(file);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var key in new[] { file.OriginalKey, file.PreviewKey, file.ThumbnailKey, file.CardKey, file.LargeKey })
            storage.Delete(key);
    }

    private async Task<(UploadError Error, string? ContentType)> DetectAsync(long length, Stream content, CancellationToken cancellationToken)
    {
        if (length == 0)
            return (UploadError.Empty, null);
        if (length > options.Value.MaxFileSizeBytes)
            return (UploadError.TooLarge, null);

        var header = new byte[ImageFormats.HeaderLength];
        var headerLength = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        var contentType = ImageFormats.DetectContentType(header.AsSpan(0, headerLength));
        if (contentType is not null)
            return (UploadError.None, contentType);
        return (ImageFormats.IsHeic(header.AsSpan(0, headerLength)) ? UploadError.Heic : UploadError.UnsupportedFormat, null);
    }

    private static string CleanFileName(string fileName)
    {
        var name = new string(Path.GetFileName(fileName).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0)
            return "upload";
        return name.Length <= 255 ? name : name[..255];
    }
}
