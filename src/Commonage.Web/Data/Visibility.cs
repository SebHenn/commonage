using System.Text.RegularExpressions;
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

    /// <summary>
    /// The stories visitors may read: published, not in the bin, and with at least one text block that has words in it (not only white space) or one item block whose item
    /// <see cref="Item"/> allows (so embargo and withdrawal come with it). This is the only place that writes
    /// <see cref="StoryStatus.Published"/> in a query. The explicit <c>DeletedAt</c> test is belt and braces on the query filter.
    /// A method on the context rather than an expression: it cannot be applied to a navigation from a block to its item.
    /// </summary>
    public static IQueryable<Story> Story(CommonageDbContext db, DateOnly today) =>
        db.Stories.Where(s => s.Status == StoryStatus.Published && s.DeletedAt == null
            && s.Blocks.Any(b => (b.Kind == StoryBlockKind.Text && b.Text != null && Regex.IsMatch(b.Text, @"\S"))
                || db.ArchiveItems.Where(Item(today)).Any(i => i.Id == b.ArchiveItemId)));

    /// <summary>
    /// The people visitors may see: not hidden, not in the bin, and linked to at least one item <see cref="Item"/> allows, so a
    /// person whose every item is a draft, embargoed, withdrawn or in the bin is not public either. This file and
    /// <c>Pages/Admin</c> are the only places that may name <see cref="PersonDisclosure"/>.
    /// </summary>
    public static IQueryable<Person> Person(CommonageDbContext db, DateOnly today) =>
        db.People.Where(p => p.Disclosure != PersonDisclosure.Hidden && p.DeletedAt == null
            && p.Links.Any(l => db.ArchiveItems.Where(Item(today)).Any(i => i.Id == l.ArchiveItemId)));

    /// <summary>The people who are not hidden, whatever their items are. For exports that carry their own item rules.</summary>
    public static IQueryable<Person> PersonNotHidden(CommonageDbContext db) =>
        db.People.Where(p => p.Disclosure != PersonDisclosure.Hidden && p.DeletedAt == null);

    /// <summary>The people marked as having died. Only their dates may be exported or shown.</summary>
    public static IQueryable<Person> PersonDeceased(IQueryable<Person> people) =>
        people.Where(p => p.Disclosure == PersonDisclosure.Deceased);
}
