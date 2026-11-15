using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Identity;

/// <summary>Reads and writes <see cref="StaffProfile"/>, which exists only once there is something to keep.</summary>
public static class StaffProfiles
{
    /// <summary>Remembers a login. Called after the person has passed every check.</summary>
    public static async Task RecordLoginAsync(CommonageDbContext db, TimeProvider clock, string userId, CancellationToken cancellationToken = default)
    {
        var profile = await db.StaffProfiles.FindAsync([userId], cancellationToken);
        if (profile is null)
            db.StaffProfiles.Add(profile = new StaffProfile { UserId = userId });
        profile.LastLoginAt = clock.GetUtcNow().UtcDateTime;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two logins at the same moment both tried to add the row. A time that is a second off must not fail a valid login.
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>Sets the name to show. Empty means none. Returns false when it did not change.</summary>
    public static async Task<bool> SetDisplayNameAsync(CommonageDbContext db, string userId, string? name, CancellationToken cancellationToken = default)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        var profile = await db.StaffProfiles.FindAsync([userId], cancellationToken);
        if (profile is null)
        {
            if (name is null)
                return false;
            db.StaffProfiles.Add(new StaffProfile { UserId = userId, DisplayName = name });
            return true;
        }
        if (profile.DisplayName == name)
            return false;
        profile.DisplayName = name;
        return true;
    }
}
