using System.Globalization;
using Commonage.Web.Media;
using Microsoft.Extensions.Options;

namespace Commonage.Web.Operations;

public class BackupOptions
{
    public const string Section = "Backups";

    /// <summary>Admins get a notice when the last backup is older than this.</summary>
    public int WarnAfterDays { get; set; } = 3;
}

public interface IDiskSpace
{
    /// <summary>Bytes free on the disk that holds this path, or null if that cannot be found out.</summary>
    long? FreeBytes(string path);
}

public class SystemDiskSpace(ILogger<SystemDiskSpace> logger) : IDiskSpace
{
    public long? FreeBytes(string path)
    {
        try
        {
            var existing = path;
            while (!Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent)
                existing = parent;
            return new DriveInfo(existing).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not find out how much disk space is free at {Path}.", path);
            return null;
        }
    }
}

/// <summary>
/// What admins need to know to keep an archive safe: free disk space and the age of the last backup. A full disk also
/// stops the database, so uploads are refused well before that.
/// </summary>
public class ArchiveHealth(
    string storageRoot,
    IDiskSpace disk,
    IOptions<UploadOptions> uploads,
    IOptions<BackupOptions> backups,
    TimeProvider clock)
{
    private const long BytesPerMegabyte = 1024 * 1024;

    /// <summary>Uploads wait in the temporary folder while a request arrives, which can be on another disk.</summary>
    private static string TemporaryFolder => Environment.GetEnvironmentVariable("ASPNETCORE_TEMP") ?? Path.GetTempPath();

    /// <summary>Written by scripts/backup.sh next to the media folder, so restoring an old backup does not bring back an old date.</summary>
    private string BackupStampPath => Path.Combine(Path.GetDirectoryName(storageRoot) ?? storageRoot, "last-backup");

    /// <summary>The space left on the fuller of the two disks, or null if unknown.</summary>
    public long? FreeBytes
    {
        get
        {
            var media = disk.FreeBytes(storageRoot);
            var temporary = disk.FreeBytes(TemporaryFolder);
            return media is null || temporary is null ? media ?? temporary : Math.Min(media.Value, temporary.Value);
        }
    }

    /// <summary>
    /// Whether an upload of this size fits. It counts twice: once while it waits in the temporary folder, once when
    /// it is stored. If free space cannot be found out, uploads stay possible rather than stopping an archive for that.
    /// </summary>
    public bool CanAcceptUploads(long incomingBytes = 0) =>
        FreeBytes is not { } free || free - (2 * incomingBytes) >= uploads.Value.MinimumFreeMegabytes * BytesPerMegabyte;

    public bool IsLowOnSpace => FreeBytes is { } free && free < uploads.Value.WarnFreeMegabytes * BytesPerMegabyte;

    public DateTime? LastBackupAt
    {
        get
        {
            try
            {
                return DateTimeOffset.TryParse(
                    File.ReadAllText(BackupStampPath).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                    ? at.UtcDateTime
                    : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Bytes in the media folder: the originals, every picture size made from them, and the logo. Reads the whole folder,
    /// so whoever asks keeps the answer for a while.
    /// </summary>
    public long MeasureStorageBytes()
    {
        if (!Directory.Exists(storageRoot))
            return 0;
        var everything = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        return new DirectoryInfo(storageRoot).EnumerateFiles("*", everything).Sum(f => f.Length);
    }

    public bool BackupIsOverdue =>
        LastBackupAt is not { } at || clock.GetUtcNow().UtcDateTime - at > TimeSpan.FromDays(backups.Value.WarnAfterDays);

    /// <summary>Whole days since the last backup, if there was one.</summary>
    public int? DaysSinceLastBackup => LastBackupAt is { } at ? (int)(clock.GetUtcNow().UtcDateTime - at).TotalDays : null;
}

/// <summary>Marks a page whose forms can upload files, so <see cref="UploadGuard"/> looks at them.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AcceptsUploadsAttribute : Attribute;

public static class UploadGuard
{
    /// <summary>
    /// Refuses an upload before any of it is read. ASP.NET would otherwise write the whole request to the temporary
    /// folder first, which is exactly what a nearly full disk cannot take.
    /// </summary>
    public static IApplicationBuilder UseUploadGuard(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (HttpMethods.IsPost(request.Method)
                && request.ContentType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true
                && context.GetEndpoint()?.Metadata.GetMetadata<AcceptsUploadsAttribute>() is not null
                && !context.RequestServices.GetRequiredService<ArchiveHealth>().CanAcceptUploads(request.ContentLength ?? 0))
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(UploadGuard))
                    .LogWarning("Refused an upload of {Bytes} bytes because the disk is nearly full.", request.ContentLength);
                context.Response.StatusCode = StatusCodes.Status507InsufficientStorage;
                return;
            }
            await next(context);
        });
}
