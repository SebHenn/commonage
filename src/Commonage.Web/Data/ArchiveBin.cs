using Commonage.Web.Audit;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Data;

/// <summary>
/// Deleted items and photos wait in the bin before they are gone for good, so a mistake can be undone. Nothing else in
/// the app sees them, because the query filters in <see cref="CommonageDbContext"/> leave them out.
/// </summary>
public class ArchiveBin(CommonageDbContext db, MediaUploadService uploads, AuditLog audit, TimeProvider clock)
{
    public const int RetentionDays = 30;

    public static DateTime GoneForGoodOn(DateTime movedToBinAt) => movedToBinAt.AddDays(RetentionDays);

    /// <summary>Deletes an item in the bin with all its files, including photos that were in the bin on their own.</summary>
    public async Task DeleteItemAsync(ArchiveItem item, AuditAction action, CancellationToken cancellationToken = default)
    {
        foreach (var file in item.MediaFiles.ToList())
            await uploads.DeleteAsync(file, cancellationToken);
        // Deleting the item keeps this entry and clears its link to the item; the subject keeps the title.
        audit.Record(action, item.Title);
        db.ArchiveItems.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeletePhotoAsync(MediaFile file, AuditAction action, CancellationToken cancellationToken = default)
    {
        audit.Record(action, file.ArchiveItem.Title, file.ArchiveItem);
        await uploads.DeleteAsync(file, cancellationToken);
    }

    /// <summary>Deletes what has been in the bin for longer than <see cref="RetentionDays"/>.</summary>
    public async Task PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-RetentionDays);

        var items = await db.BinnedItems
            .Include(i => i.MediaFiles)
            .Where(i => i.DeletedAt <= cutoff)
            .ToListAsync(cancellationToken);
        foreach (var item in items)
            await DeleteItemAsync(item, AuditAction.ItemPurged, cancellationToken);

        var photos = await db.BinnedPhotos
            .Include(f => f.ArchiveItem)
            .Where(f => f.DeletedAt <= cutoff)
            .ToListAsync(cancellationToken);
        foreach (var photo in photos)
            await DeletePhotoAsync(photo, AuditAction.PhotoPurged, cancellationToken);
    }
}

/// <summary>
/// At startup and then a few times a day: moves rejected and withdrawn contributions to the bin and shortens the activity
/// log, if the archive keeps them for a set time (<see cref="ArchiveRetention"/>), then empties what has expired from the bin.
/// </summary>
public class ArchiveBinPurger(IServiceScopeFactory scopeFactory, ILogger<ArchiveBinPurger> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ArchiveRetention>().ApplyAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The next round tries again; nothing is lost by waiting.
                logger.LogError(exception, "Could not apply the times the archive keeps records.");
            }

            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ArchiveBin>().PurgeExpiredAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Could not empty the bin.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
