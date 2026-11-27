using System.Globalization;
using Commonage.Web.Domain;
using Commonage.Web.Search;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Data;

/// <summary>An other name of a person that a curator marked for visitors.</summary>
public record PublicPersonName(string Name, PersonNameKind Kind, string? Language);

/// <summary>A person in a line on an item page: the name and the address, nothing else.</summary>
public record PersonCard(string PublicId, string Name);

/// <summary>A person in the index with the number of items visitors may see of them.</summary>
public record PersonIndexEntry(string PublicId, string Name, int Count, bool MayBeLiving);

/// <summary>
/// What visitors may be told about a person. It has no field for the curator's note, when the choice was made, the sort name or
/// the internal id: a page cannot show what the record does not hold. Dates are only here for a person marked as having died.
/// <paramref name="HasDied"/> is false for a person who may be living, whose page is then not for search engines.
/// </summary>
public record PersonView(
    string PublicId,
    string Name,
    bool HasDied,
    IReadOnlyList<PublicPersonName> OtherNames,
    string? Biography,
    string? BornText,
    string? DiedText,
    PagedList<ItemSummary> Items);

/// <summary>
/// The only way a page reads a person for visitors. Queries start from <see cref="CommonageDbContext.VisiblePeople"/>; the
/// curator's preview starts from the person's own row and goes through the same projection, so it shows no more than a
/// visitor would see once the person is shown.
/// </summary>
public static class VisiblePeople
{
    /// <summary>How many items a page of a person shows.</summary>
    public const int PageSize = 24;

    /// <summary>What the page reads of the person row, before the items: no note, no sort name, no choice date, no internal id.</summary>
    public record PersonRow(
        string PublicId, string Name, string? Biography, string? BornText, string? DiedText, List<PublicPersonName> Names);

    /// <summary>The query behind <see cref="ViewAsync"/>, public so that a test can read the SQL it makes.</summary>
    public static IQueryable<PersonRow> Projection(IQueryable<Person> people, string publicId) =>
        people
            .Where(p => p.PublicId == publicId)
            .Select(p => new PersonRow(
                p.PublicId,
                p.Name,
                p.Biography,
                p.BornText,
                p.DiedText,
                p.Names.Where(n => n.ShownToVisitors).OrderBy(n => n.Id).Select(n => new PublicPersonName(n.Name, n.Kind, n.Language)).ToList()))
            .AsNoTracking();

    /// <summary>The items of a person that visitors may see, oldest first: the query behind a page, public for the SQL test.</summary>
    public static IQueryable<ItemSummary> ItemsQuery(CommonageDbContext db, string publicId) =>
        db.VisibleItems.AsNoTracking()
            .Where(i => i.People.Any(l => l.Person.PublicId == publicId))
            .Sort(ItemSort.OldestFirst, null)
            .Select(ItemSummary.FromItem);

    /// <summary>
    /// The person as visitors see them, with one page of the items visitors may see. Null when there is no such person in
    /// <paramref name="people"/>. Public pages pass <see cref="CommonageDbContext.VisiblePeople"/> (see <see cref="PageAsync"/>), so
    /// an unknown key, a hidden or binned person and one whose items are all hidden are all the same null.
    /// </summary>
    public static async Task<PersonView?> ViewAsync(
        CommonageDbContext db, IQueryable<Person> people, string publicId, int page, CancellationToken cancellationToken)
    {
        var row = await Projection(people, publicId).FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;

        // Asked of Visibility, the one place that names the choices: only someone who has died has dates visitors may see.
        var died = await Visibility.PersonDeceased(people).AnyAsync(p => p.PublicId == publicId, cancellationToken);

        var items = await ItemsQuery(db, publicId).ToPagedListAsync(page, PageSize, cancellationToken);
        return new PersonView(row.PublicId, row.Name, died, row.Names, row.Biography, died ? row.BornText : null, died ? row.DiedText : null, items);
    }

    /// <summary>The person of an address for visitors, or null: the one call a public person page makes.</summary>
    public static Task<PersonView?> PageAsync(CommonageDbContext db, string publicId, int page, CancellationToken cancellationToken) =>
        ViewAsync(db, db.VisiblePeople, publicId, page, cancellationToken);

    private record Row(string PublicId, string Name, string? Sort, int Count, bool MayBeLiving = false);

    private static List<T> InNameOrder<T>(IEnumerable<Row> rows, Func<Row, T> make)
    {
        var order = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
        return rows
            .OrderBy(r => string.IsNullOrWhiteSpace(r.Sort) ? r.Name : r.Sort, order)
            .ThenBy(r => r.Name, order)
            .ThenBy(r => r.PublicId, StringComparer.Ordinal)
            .Select(make)
            .ToList();
    }

    /// <summary>The public people of one item that visitors already see, in name order, for the item page.</summary>
    public static async Task<IReadOnlyList<PersonCard>> OfItemAsync(CommonageDbContext db, int itemId, CancellationToken cancellationToken)
    {
        var rows = await db.VisiblePeople.AsNoTracking()
            .Where(p => p.Links.Any(l => l.ArchiveItemId == itemId))
            .Select(p => new Row(p.PublicId, p.Name, p.SortName, 0))
            .ToListAsync(cancellationToken);
        return InNameOrder(rows, r => new PersonCard(r.PublicId, r.Name));
    }

    /// <summary>The main names of the people of an item who have died, for the JSON and Dublin Core. Nobody who may be living.</summary>
    public static async Task<IReadOnlyList<string>> DeceasedOfItemAsync(CommonageDbContext db, int itemId, CancellationToken cancellationToken)
    {
        var rows = await Visibility.PersonDeceased(db.VisiblePeople).AsNoTracking()
            .Where(p => p.Links.Any(l => l.ArchiveItemId == itemId))
            .Select(p => new Row(p.PublicId, p.Name, p.SortName, 0))
            .ToListAsync(cancellationToken);
        return InNameOrder(rows, r => r.Name);
    }

    /// <summary>
    /// Every public person with the number of items visitors may see of them, from A to Z by the way a curator filed the name.
    /// Everything comes from visible rows: a hidden person or item changes no name, letter or count.
    /// </summary>
    public static async Task<IReadOnlyList<PersonIndexEntry>> IndexAsync(CommonageDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.VisiblePeople.AsNoTracking()
            .Select(p => new Row(p.PublicId, p.Name, p.SortName, p.Links.Count(l => db.VisibleItems.Any(i => i.Id == l.ArchiveItemId)), p.Disclosure != PersonDisclosure.Deceased))
            .ToListAsync(cancellationToken);
        return InNameOrder(rows, r => new PersonIndexEntry(r.PublicId, r.Name, r.Count, r.MayBeLiving));
    }

    /// <summary>Whether any person is public: what decides if a page offers "All people".</summary>
    public static Task<bool> AnyAsync(CommonageDbContext db, CancellationToken cancellationToken) =>
        db.VisiblePeople.AnyAsync(cancellationToken);

    /// <summary>The addresses of the public people who have died, for the sitemap. Nobody who may be living.</summary>
    public static Task<List<string>> DeceasedKeysAsync(CommonageDbContext db, int most, CancellationToken cancellationToken) =>
        Visibility.PersonDeceased(db.VisiblePeople).OrderBy(p => p.PublicId).Select(p => p.PublicId).Take(most).ToListAsync(cancellationToken);

    /// <summary>
    /// The items that have a public person whose main name, or another name marked for visitors, matches the search. Computed from
    /// the rows as they are now (nothing is stored), so a change of choice, a name taken back or the bin is seen by the next search.
    /// The item's own rule is the caller's: it passes only visible items. Folded names are what the search folds everything to.
    /// </summary>
    public static IQueryable<int> ItemIdsByName(CommonageDbContext db, string query) =>
        db.VisiblePeople
            .Where(p => EF.Functions.ToTsVector(CommonageDbContext.SearchConfiguration, p.FoldedName)
                    .Matches(EF.Functions.ToTsQuery(CommonageDbContext.SearchConfiguration, query))
                || p.Names.Any(n => n.ShownToVisitors
                    && EF.Functions.ToTsVector(CommonageDbContext.SearchConfiguration, n.FoldedName)
                        .Matches(EF.Functions.ToTsQuery(CommonageDbContext.SearchConfiguration, query))))
            .SelectMany(p => p.Links.Select(l => l.ArchiveItemId));

    /// <summary>The ids of the items of a person that visitors may see, for a curator's page that says which are not.</summary>
    public static Task<List<int>> VisibleItemIdsAsync(CommonageDbContext db, int personId, CancellationToken cancellationToken) =>
        db.ArchiveItems.AsNoTracking()
            .Where(Visibility.Item(db.Today))
            .Where(i => i.People.Any(l => l.PersonId == personId))
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);
}
