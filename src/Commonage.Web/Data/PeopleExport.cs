using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Data;

/// <summary>
/// Which people an export holds and how they are read, so that the exporter never queries people itself. A copy of what is
/// public holds only the people visitors may see; a copy of everything without personal data holds no hidden person; a copy with
/// personal data holds everyone who is not in the bin. What each kind leaves out of a person is decided by the exporter, from
/// the rows read here, and the item lists and the spreadsheet column are built from the same set.
/// </summary>
public static class PeopleExport
{
    public static IQueryable<Person> Select(CommonageDbContext db, bool publishedOnly, bool includePersonalData, DateOnly today) =>
        publishedOnly ? Visibility.Person(db, today)
        : includePersonalData ? db.People
        : Visibility.PersonNotHidden(db);

    /// <summary>A batch of people after the given id, in the order of their ids, with their other names.</summary>
    public static Task<List<Person>> BatchAsync(IQueryable<Person> people, int after, int size, CancellationToken cancellationToken) =>
        people.AsNoTracking().Include(p => p.Names).AsSplitQuery()
            .Where(p => p.Id > after).OrderBy(p => p.Id).Take(size).ToListAsync(cancellationToken);

    /// <summary>The main names of the people in the set, for the column of the spreadsheet.</summary>
    public static async Task<Dictionary<int, string>> NamesAsync(IQueryable<Person> people, CancellationToken cancellationToken) =>
        (await people.AsNoTracking().Select(p => new { p.Id, p.Name }).ToListAsync(cancellationToken)).ToDictionary(p => p.Id, p => p.Name);

    /// <summary>For each item, the ids of the people of the set linked to it.</summary>
    public static async Task<ILookup<int, int>> LinksAsync(
        CommonageDbContext db, IQueryable<Person> people, IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken) =>
        (await db.ItemPeople.AsNoTracking()
            .Where(l => itemIds.Contains(l.ArchiveItemId) && people.Any(p => p.Id == l.PersonId))
            .OrderBy(l => l.ArchiveItemId).ThenBy(l => l.PersonId)
            .Select(l => new { l.ArchiveItemId, l.PersonId })
            .ToListAsync(cancellationToken))
        .ToLookup(l => l.ArchiveItemId, l => l.PersonId);
}
