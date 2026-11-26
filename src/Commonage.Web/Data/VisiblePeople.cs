using Commonage.Web.Domain;
using Commonage.Web.Search;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Data;

/// <summary>An other name of a person that a curator marked for visitors.</summary>
public record PublicPersonName(string Name, PersonNameKind Kind, string? Language);

/// <summary>
/// What visitors may be told about a person. It has no field for the curator's note, when the choice was made, the sort name or
/// the internal id: a page cannot show what the record does not hold. Dates are only here for a person marked as having died.
/// </summary>
public record PersonView(
    string PublicId,
    string Name,
    IReadOnlyList<PublicPersonName> OtherNames,
    string? Biography,
    string? BornText,
    string? DiedText,
    IReadOnlyList<ItemSummary> Items);

/// <summary>
/// The only way a page reads a person for visitors. Queries start from <see cref="CommonageDbContext.VisiblePeople"/>; the
/// curator's preview starts from the person's own row and goes through the same projection, so it shows no more than a
/// visitor would see once the person is shown.
/// </summary>
public static class VisiblePeople
{
    /// <summary>The person as visitors see them, with the items visitors may see. Null when there is no such person in <paramref name="people"/>.</summary>
    public static async Task<PersonView?> ViewAsync(CommonageDbContext db, IQueryable<Person> people, string publicId, CancellationToken cancellationToken)
    {
        var row = await people
            .Where(p => p.PublicId == publicId)
            .Select(p => new
            {
                p.Id,
                p.PublicId,
                p.Name,
                p.Biography,
                Died = p.Disclosure == PersonDisclosure.Deceased,
                p.BornText,
                p.DiedText,
                Names = p.Names.Where(n => n.ShownToVisitors).OrderBy(n => n.Id).Select(n => new PublicPersonName(n.Name, n.Kind, n.Language)).ToList(),
            })
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;

        var items = await db.ArchiveItems.AsNoTracking()
            .Where(Visibility.Item(db.Today))
            .Where(i => i.People.Any(l => l.PersonId == row.Id))
            .OrderBy(i => i.DateFrom).ThenBy(i => i.Title).ThenBy(i => i.Id)
            .Select(ItemSummary.FromItem)
            .ToListAsync(cancellationToken);
        return new PersonView(row.PublicId, row.Name, row.Names, row.Biography, row.Died ? row.BornText : null, row.Died ? row.DiedText : null, items);
    }

    /// <summary>The ids of the items of a person that visitors may see, for a curator's page that says which are not.</summary>
    public static Task<List<int>> VisibleItemIdsAsync(CommonageDbContext db, int personId, CancellationToken cancellationToken) =>
        db.ArchiveItems.AsNoTracking()
            .Where(Visibility.Item(db.Today))
            .Where(i => i.People.Any(l => l.PersonId == personId))
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);
}
