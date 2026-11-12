using System.Globalization;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Commonage.Web.Operations;
using Commonage.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin;

public record FailedPreview(int Id, int ItemId, string ItemTitle, string FileName, string? Error, DateTime UploadedAt);

/// <summary>
/// What whoever looks after the archive needs to see in one place: the version, the checks behind /health, disk use and
/// how fast it grows, previews that could not be made, and the database.
/// </summary>
public class SystemModel(
    CommonageDbContext db,
    ArchiveHealth health,
    HealthCheckService healthChecks,
    MediaProcessingQueue previews,
    LoginKeyStatus loginKeys,
    AuditLog audit,
    IMemoryCache cache,
    TimeProvider clock,
    IStringLocalizer<SharedResource> localizer) : PageModel
{
    public const int MaxFailedShown = 200;

    /// <summary>How far back the pace of growth is measured, at most.</summary>
    public const int GrowthDays = 90;

    public string Version => AppVersion.Current;

    public IReadOnlyList<(string Name, HealthStatus Status)> Checks { get; private set; } = [];
    public HealthStatus Overall { get; private set; }

    public long? FreeBytes { get; private set; }
    public long StorageBytes { get; private set; }
    public long OriginalsBytes { get; private set; }
    public int OriginalsCount { get; private set; }
    public long AddedLast30DaysBytes { get; private set; }
    public int AddedLast30DaysCount { get; private set; }

    /// <summary>The days the pace was measured over, and how many months the free space lasts at that pace. Null without growth.</summary>
    public (int Days, double Months)? FreeSpaceLasts { get; private set; }

    public long DatabaseBytes { get; private set; }
    public string? PostgresVersion { get; private set; }
    public string? LastMigration { get; private set; }
    public DateTime? LastMigrationAt { get; private set; }
    public int PendingMigrations { get; private set; }
    public bool UsesUnaccent { get; private set; }

    public DateTime? LastBackupAt { get; private set; }
    public bool BackupIsOverdue { get; private set; }
    public DateTime? RestoreTestedAt { get; private set; }
    public bool LoginKeyEncrypted => loginKeys.Encrypted;

    public IReadOnlyList<FailedPreview> Failed { get; private set; } = [];
    public int FailedCount { get; private set; }
    public int WaitingCount { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var report = await healthChecks.CheckHealthAsync(cancellationToken);
        Overall = report.Status;
        Checks = report.Entries.Select(e => (e.Key, e.Value.Status)).ToList();

        var now = clock.GetUtcNow().UtcDateTime;
        FreeBytes = health.FreeBytes;
        // Reading every file of a large archive takes a moment, so the answer is kept for a quarter of an hour.
        StorageBytes = await cache.GetOrCreateAsync("system:storage-bytes", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
            return Task.Run(health.MeasureStorageBytes, cancellationToken);
        });

        // Files in the bin are still on the disk, so they count.
        var files = db.MediaFiles.IgnoreQueryFilters().AsNoTracking();
        OriginalsBytes = await files.SumAsync(f => f.SizeBytes, cancellationToken);
        OriginalsCount = await files.CountAsync(cancellationToken);
        var monthAgo = now.AddDays(-30);
        AddedLast30DaysBytes = await files.Where(f => f.UploadedAt >= monthAgo).SumAsync(f => f.SizeBytes, cancellationToken);
        AddedLast30DaysCount = await files.Where(f => f.UploadedAt >= monthAgo).CountAsync(cancellationToken);
        FreeSpaceLasts = await EstimateAsync(files, now, cancellationToken);

        var database = db.Database;
        DatabaseBytes = await database.SqlQuery<long>($"""SELECT pg_database_size(current_database()) AS "Value" """).SingleAsync(cancellationToken);
        PostgresVersion = await database.SqlQuery<string>($"""SELECT current_setting('server_version') AS "Value" """).SingleAsync(cancellationToken);
        LastMigration = (await database.GetAppliedMigrationsAsync(cancellationToken)).LastOrDefault();
        LastMigrationAt = LastMigration is { Length: > 14 } id
            && DateTime.TryParseExact(id[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            ? at : null;
        PendingMigrations = (await database.GetPendingMigrationsAsync(cancellationToken)).Count();
        UsesUnaccent = await AccentFolding.UsesUnaccentAsync(db, cancellationToken);

        LastBackupAt = health.LastBackupAt;
        BackupIsOverdue = health.BackupIsOverdue;
        RestoreTestedAt = (await db.ArchiveSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken))?.RestoreTestedAt;

        var failed = db.MediaFiles.AsNoTracking().Where(f => f.Status == ProcessingStatus.Failed);
        FailedCount = await failed.CountAsync(cancellationToken);
        Failed = await failed
            .OrderByDescending(f => f.UploadedAt)
            .Take(MaxFailedShown)
            .Select(f => new FailedPreview(f.Id, f.ArchiveItemId, f.ArchiveItem.Title, f.OriginalFileName, f.ProcessingError, f.UploadedAt))
            .ToListAsync(cancellationToken);
        WaitingCount = await db.MediaFiles.AsNoTracking().CountAsync(f => f.Status == ProcessingStatus.Pending, cancellationToken);
    }

    /// <summary>
    /// How long the free space lasts if uploads go on as over the last <see cref="GrowthDays"/> days, or since the first
    /// upload if that is more recent. Each original also brings its picture sizes, in the same proportion as so far.
    /// </summary>
    private async Task<(int Days, double Months)?> EstimateAsync(IQueryable<MediaFile> files, DateTime now, CancellationToken cancellationToken)
    {
        if (FreeBytes is not { } free || OriginalsBytes == 0)
            return null;
        var first = await files.MinAsync(f => (DateTime?)f.UploadedAt, cancellationToken) ?? now;
        var days = (int)Math.Clamp(Math.Ceiling((now - first).TotalDays), 7, GrowthDays);
        var since = now.AddDays(-days);
        var added = await files.Where(f => f.UploadedAt >= since).SumAsync(f => f.SizeBytes, cancellationToken);
        if (added == 0)
            return null;
        var withPictures = added * Math.Max(1.0, (double)StorageBytes / OriginalsBytes);
        return (days, free / (withPictures / days) / 30.4);
    }

    public async Task<IActionResult> OnPostRetryAsync(int? id, CancellationToken cancellationToken)
    {
        var failed = await db.MediaFiles
            .Include(f => f.ArchiveItem)
            .Where(f => f.Status == ProcessingStatus.Failed && (id == null || f.Id == id))
            .ToListAsync(cancellationToken);
        if (failed.Count == 0)
            return id is null ? RedirectToPage() : NotFound();

        foreach (var file in failed)
        {
            file.Status = ProcessingStatus.Pending;
            file.ProcessingError = null;
            file.ProcessingAttempts = 0;
        }
        foreach (var item in failed.Select(f => f.ArchiveItem).DistinctBy(i => i.Id))
            audit.Record(AuditAction.PreviewRetried, item.Title, item);
        await db.SaveChangesAsync(cancellationToken);
        failed.ForEach(f => previews.Enqueue(f.Id));

        TempData["Message"] = localizer.Plural("{0, plural, one {The preview is being made again.} other {# previews are being made again.}}", failed.Count);
        return RedirectToPage();
    }
}
