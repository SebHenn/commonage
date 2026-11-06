using System.Security.Claims;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Audit;

/// <summary>
/// Records what curators and administrators do. Entries are added to the current <see cref="CommonageDbContext"/>,
/// so they are saved together with the change they describe, or not at all.
/// </summary>
public class AuditLog(CommonageDbContext db, IHttpContextAccessor httpContextAccessor, TimeProvider clock)
{
    /// <summary>Photos arrive one at a time now. Forty of them in one sitting are one thing that happened to an item, not forty.</summary>
    public Task RecordPhotosAddedAsync(ArchiveItem item, CancellationToken cancellationToken) =>
        RecordOncePerSittingAsync(AuditAction.PhotosAdded, item, cancellationToken);

    /// <summary>The same goes for putting forty photos in order.</summary>
    public Task RecordPhotosArrangedAsync(ArchiveItem item, CancellationToken cancellationToken) =>
        RecordOncePerSittingAsync(AuditAction.PhotosArranged, item, cancellationToken);

    private async Task RecordOncePerSittingAsync(AuditAction action, ArchiveItem item, CancellationToken cancellationToken)
    {
        var userId = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var since = clock.GetUtcNow().UtcDateTime.AddMinutes(-30);
        if (!await db.AuditEntries.AnyAsync(e => e.ArchiveItemId == item.Id && e.Action == action && e.UserId == userId && e.At > since, cancellationToken))
            Record(action, item.Title, item);
    }

    public void Record(AuditAction action, string? subject, ArchiveItem? item = null)
    {
        var user = httpContextAccessor.HttpContext?.User;
        var signedIn = user?.Identity?.IsAuthenticated == true;
        db.AuditEntries.Add(new AuditEntry
        {
            At = clock.GetUtcNow().UtcDateTime,
            UserId = signedIn ? user!.FindFirstValue(ClaimTypes.NameIdentifier) : null,
            UserName = signedIn ? user!.Identity!.Name : null,
            Action = action,
            ArchiveItem = item,
            Subject = subject is { Length: > AuditEntry.SubjectMaxLength } ? subject[..AuditEntry.SubjectMaxLength] : subject,
        });
    }
}
