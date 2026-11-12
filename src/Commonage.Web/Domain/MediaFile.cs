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

    /// <summary>SHA-256 of the original, hex-encoded. Lets backups and exports verify the file is intact, and curators notice a file the archive already has.</summary>
    public required string Sha256 { get; set; }

    /// <summary>Random name under which the original and its previews are stored.</summary>
    public Guid StorageId { get; set; }

    public ProcessingStatus Status { get; set; }
    public string? ProcessingError { get; set; }

    /// <summary>
    /// How often making the previews was begun, counted before the file is opened. A file that stops the whole app, by
    /// using up the memory or by crashing the image library, would otherwise be the first thing the app tries after
    /// every restart.
    /// </summary>
    public int ProcessingAttempts { get; set; }

    public const int MaxProcessingAttempts = 3;

    public int? PreviewWidth { get; set; }
    public int? PreviewHeight { get; set; }

    public DateTime UploadedAt { get; set; }

    /// <summary>What the photo shows, for people who cannot see it. Without it, the item's title stands in.</summary>
    public string? AltText { get; set; }

    public const int MaxCaptionLength = 1000;

    /// <summary>What visitors read under the photo, unlike <see cref="AltText"/>, which replaces it for those who cannot see it.</summary>
    public string? Caption { get; set; }

    /// <summary>Where it comes among the item's photos, lowest first. The first one is the item's cover.</summary>
    public int Position { get; set; }

    /// <summary>How far curators turned it, in degrees clockwise: 0, 90, 180 or 270. The original itself is never changed.</summary>
    public int Rotation { get; set; }

    /// <summary>How far the stored previews are turned. They follow <see cref="Rotation"/> in the background and go on being served meanwhile.</summary>
    public int RenderedRotation { get; set; }

    public bool IsBeingTurned => Rotation != RenderedRotation;

    /// <summary>Whether a size larger than the preview exists. Not for a file that is no larger than the preview, or while an archive does not use it.</summary>
    public bool HasLarge { get; set; }

    /// <summary>When the photo was moved to the bin on its own. Photos in the bin are hidden from everything but the bin page.</summary>
    public DateTime? DeletedAt { get; set; }

    public string OriginalKey => $"originals/{StorageId:N}";
    public string PreviewKey => $"previews/{StorageId:N}.jpg";
    public string ThumbnailKey => $"thumbnails/{StorageId:N}.jpg";
    public string CardKey => $"cards/{StorageId:N}.jpg";
    public string LargeKey => $"large/{StorageId:N}.jpg";

    /// <summary>Which picture sizes exist. Raising it makes the archive add the new ones in the background.</summary>
    public const int CurrentDerivativeVersion = 3;

    /// <summary>Files from before the larger card size have version 1 until the archive has made it, and version 2 until it has thought about the large size.</summary>
    public int DerivativeVersion { get; set; } = 1;
}
