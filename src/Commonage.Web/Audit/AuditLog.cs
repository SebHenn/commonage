using System.Security.Claims;
using Commonage.Web.Data;
using Commonage.Web.Domain;

namespace Commonage.Web.Audit;

/// <summary>
/// Records what curators and administrators do. Entries are added to the current <see cref="CommonageDbContext"/>,
/// so they are saved together with the change they describe, or not at all.
/// </summary>
public class AuditLog(CommonageDbContext db, IHttpContextAccessor httpContextAccessor, TimeProvider clock)
{
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
