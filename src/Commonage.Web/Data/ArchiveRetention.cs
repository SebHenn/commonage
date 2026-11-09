using Commonage.Web.Audit;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Data;

/// <summary>
/// Keeps records only as long as the archive chose to, in its settings: the activity log, and contributions that were
/// rejected or withdrawn. Both are kept for good until an admin sets a time, as they were before these settings.
/// </summary>
public class ArchiveRetention(CommonageDbContext db, AuditLog audit, TimeProvider clock)
{
    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        var settings = await db.ArchiveSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
            return;
        var now = clock.GetUtcNow().UtcDateTime;

        // They go to the bin rather than straight away, so a time set too short by mistake can still be undone for 30 days.
        if (settings.RejectedKeptMonths is { } months)
        {
            var cutoff = now.AddMonths(-months);
            var expired = await db.ArchiveItems
                .Where(i => (i.Status == ItemStatus.Rejected || i.Status == ItemStatus.Withdrawn) && i.RejectedOrWithdrawnAt <= cutoff)
                .ToListAsync(cancellationToken);
            foreach (var item in expired)
            {
                item.MoveToBin(now);
                audit.Record(AuditAction.ItemMovedToBinAfterKeptTime, item.Title, item);
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        if (settings.ActivityLogKeptYears is { } years)
        {
            var cutoff = now.AddYears(-years);
            var deleted = await db.AuditEntries.Where(e => e.At < cutoff).ExecuteDeleteAsync(cancellationToken);
            // The log says that it was shortened, and that entry is the newest, so it stays for the whole time again.
            if (deleted > 0)
            {
                audit.Record(AuditAction.ActivityLogShortened, null);
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }
}
