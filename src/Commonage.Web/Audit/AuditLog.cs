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

    /// <summary>Putting the blocks of a story in order takes many moves; one sitting of them is one entry.</summary>
    public async Task RecordStoryBlocksArrangedAsync(Story story, CancellationToken cancellationToken)
    {
        var userId = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var since = clock.GetUtcNow().UtcDateTime.AddMinutes(-30);
        if (!await db.AuditEntries.AnyAsync(e => e.StoryId == story.Id && e.Action == AuditAction.StoryBlocksArranged && e.UserId == userId && e.At > since, cancellationToken))
            Record(AuditAction.StoryBlocksArranged, story.Title, story: story);
    }

    private async Task RecordOncePerSittingAsync(AuditAction action, ArchiveItem item, CancellationToken cancellationToken)
    {
        var userId = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var since = clock.GetUtcNow().UtcDateTime.AddMinutes(-30);
        if (!await db.AuditEntries.AnyAsync(e => e.ArchiveItemId == item.Id && e.Action == action && e.UserId == userId && e.At > since, cancellationToken))
            Record(action, item.Title, item);
    }

    /// <summary>Keeps what a field said before it was changed, in the same save as the change itself.</summary>
    public void RecordRevision(ArchiveItem item, RevisionField field, string? oldValue)
    {
        var user = httpContextAccessor.HttpContext?.User;
        var signedIn = user?.Identity?.IsAuthenticated == true;
        db.ItemRevisions.Add(new ItemRevision
        {
            ArchiveItem = item,
            At = clock.GetUtcNow().UtcDateTime,
            UserId = signedIn ? user!.FindFirstValue(ClaimTypes.NameIdentifier) : null,
            UserName = signedIn ? user!.Identity!.Name : null,
            Field = field,
            OldValue = oldValue,
        });
    }

    public AuditEntry Record(AuditAction action, string? subject, ArchiveItem? item = null, Story? story = null)
    {
        var user = httpContextAccessor.HttpContext?.User;
        var signedIn = user?.Identity?.IsAuthenticated == true;
        return Add(action, subject, item, signedIn ? user!.FindFirstValue(ClaimTypes.NameIdentifier) : null, signedIn ? user!.Identity!.Name : null, story);
    }

    /// <summary>For what an account does while it logs in, before the request knows who it is.</summary>
    public void RecordFor(Microsoft.AspNetCore.Identity.IdentityUser account, AuditAction action, string? subject) =>
        Add(action, subject, null, account.Id, account.UserName, null);

    private AuditEntry Add(AuditAction action, string? subject, ArchiveItem? item, string? userId, string? userName, Story? story)
    {
        var entry = new AuditEntry
        {
            At = clock.GetUtcNow().UtcDateTime,
            UserId = userId,
            UserName = userName,
            Action = action,
            ArchiveItem = item,
            Story = story,
            Subject = subject is { Length: > AuditEntry.SubjectMaxLength } ? subject[..AuditEntry.SubjectMaxLength] : subject,
        };
        db.AuditEntries.Add(entry);
        return entry;
    }
}
