namespace Commonage.Web.Domain;

public enum ProcessingStatus
{
    Pending = 0,
    Ready = 1,
    Failed = 2,
}

/// <summary>
/// An uploaded file attached to an archive item. The original is stored unchanged and only curators can
/// download it. Visitors see generated previews, and only while the item is published.
/// </summary>
public class MediaFile
{
    public int Id { get; set; }

    public int ArchiveItemId { get; set; }
    public ArchiveItem ArchiveItem { get; set; } = null!;

    /// <summary>The name the file had on the uploader's computer. Shown to curators, never used as a path.</summary>
    public required string OriginalFileName { get; set; }

    /// <summary>Detected from the file's content, not taken from the browser.</summary>
    public required string ContentType { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the original, hex-encoded. Lets backups and exports verify the file is intact.</summary>
    public required string Sha256 { get; set; }

    /// <summary>Random name under which the original and its previews are stored.</summary>
    public Guid StorageId { get; set; }

    public ProcessingStatus Status { get; set; }
    public string? ProcessingError { get; set; }
    public int? PreviewWidth { get; set; }
    public int? PreviewHeight { get; set; }

    public DateTime UploadedAt { get; set; }

    public string OriginalKey => $"originals/{StorageId:N}";
    public string PreviewKey => $"previews/{StorageId:N}.jpg";
    public string ThumbnailKey => $"thumbnails/{StorageId:N}.jpg";
}
