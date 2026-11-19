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
    /// <summary>An item visitors may see: published and, by the query filter, not in the bin.</summary>
    public static readonly Expression<Func<ArchiveItem, bool>> Item = i => i.Status == ItemStatus.Published;

    /// <summary>A file visitors may be given: it belongs to a visible item.</summary>
    public static readonly Expression<Func<MediaFile, bool>> Media = f => f.ArchiveItem.Status == ItemStatus.Published;
}
