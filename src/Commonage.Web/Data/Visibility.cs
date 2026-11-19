using System.Linq.Expressions;
using Commonage.Web.Domain;

namespace Commonage.Web.Data;

/// <summary>
/// The one definition of what the public may see. This file, <see cref="CommonageDbContext.VisibleItems"/> and
/// <see cref="CommonageDbContext.VisibleMedia"/> are the only places that may write <see cref="ItemStatus.Published"/> in a
/// query. A query that starts from a collection or a tag and reaches its items uses <see cref="Item"/> and <see cref="Media"/>.
/// </summary>
public static class Visibility
{
    /// <summary>
    /// An item visitors may see: published, not in the bin (by the query filter) and not embargoed. <paramref name="today"/> is
    /// the day in the archive's time zone, <see cref="CommonageDbContext.Today"/>; an embargo ends at the start of its day.
    /// </summary>
    public static Expression<Func<ArchiveItem, bool>> Item(DateOnly today) =>
        i => i.Status == ItemStatus.Published && (i.PublicFrom == null || i.PublicFrom <= today);

    /// <summary>A visible item whose media visitors may also have. Covers and first photos come from these only.</summary>
    public static Expression<Func<ArchiveItem, bool>> ItemWithMedia(DateOnly today) =>
        i => i.Status == ItemStatus.Published && (i.PublicFrom == null || i.PublicFrom <= today) && i.MediaLevel == MediaLevel.Everything;

    /// <summary>A file visitors may be given: it belongs to a visible item that shows its media.</summary>
    public static Expression<Func<MediaFile, bool>> Media(DateOnly today) =>
        f => f.ArchiveItem.Status == ItemStatus.Published && (f.ArchiveItem.PublicFrom == null || f.ArchiveItem.PublicFrom <= today)
            && f.ArchiveItem.MediaLevel == MediaLevel.Everything;
}
