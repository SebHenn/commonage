using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Data;

/// <summary>One person a typed name matches, with what a curator needs to tell two of the same name apart.</summary>
public record PersonMatch(int Id, string Name, string? BornText, string? DiedText, int Items);

/// <summary>
/// What curators do with people that more than one page needs: finding by name, making a person, linking and unlinking. It lives
/// in <c>Data</c> because only this folder and the admin pages may query people. Linking saves only <see cref="ItemPerson"/> rows,
/// never the item, so the item's version and its editing conflicts are untouched.
/// </summary>
public static class PeopleEditing
{
    /// <summary>The text as the database folds it for searching: accents and case ignored.</summary>
    public static Task<string> FoldAsync(this CommonageDbContext db, string text, CancellationToken cancellationToken = default) =>
        db.Database.SqlQuery<string>($"SELECT lower(commonage_fold({text})) AS \"Value\"").SingleAsync(cancellationToken);

    /// <summary>People whose name or other name is this name, ignoring accents and case. Curators see all of them, hidden ones too.</summary>
    public static async Task<List<PersonMatch>> FindByNameAsync(this CommonageDbContext db, string name, CancellationToken cancellationToken = default)
    {
        var folded = await db.FoldAsync(name, cancellationToken);
        return await db.People.AsNoTracking()
            .Where(p => p.FoldedName == folded || p.Names.Any(n => n.FoldedName == folded))
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .Select(p => new PersonMatch(p.Id, p.Name, p.BornText, p.DiedText, p.Links.Count))
            .Take(50)
            .ToListAsync(cancellationToken);
    }

    /// <summary>People whose name or other name begins with the text, ignoring accents and case, for the suggestions and the list.</summary>
    public static async Task<List<PersonMatch>> SuggestAsync(this CommonageDbContext db, string text, int limit, CancellationToken cancellationToken = default)
    {
        var folded = await db.FoldAsync(text, cancellationToken);
        var pattern = LikePrefix(folded);
        return await db.People.AsNoTracking()
            .Where(p => EF.Functions.Like(p.FoldedName, pattern, "\\") || p.Names.Any(n => EF.Functions.Like(n.FoldedName, pattern, "\\")))
            .OrderBy(p => p.Name).ThenBy(p => p.Id)
            .Select(p => new PersonMatch(p.Id, p.Name, p.BornText, p.DiedText, p.Links.Count))
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public static string LikePrefix(string folded) =>
        folded.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

    /// <summary>Adds a new person who nobody but curators can see. The caller records the audit line and saves.</summary>
    public static Person AddHiddenPerson(this CommonageDbContext db, string name, DateTime now)
    {
        var person = new Person { Name = name, CreatedAt = now, UpdatedAt = now };
        db.People.Add(person);
        return person;
    }

    /// <summary>True when a tag of the kind Person has this name, ignoring accents and case: the public tag still shows on visitors' pages.</summary>
    public static async Task<bool> PublicPersonTagNamedAsync(this CommonageDbContext db, string name, CancellationToken cancellationToken = default)
    {
        var folded = await db.FoldAsync(name, cancellationToken);
        var kind = (int)TagKind.Person;
        return await db.Database
            .SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM \"Tags\" WHERE \"Kind\" = {kind} AND lower(commonage_fold(\"Name\")) = {folded}) AS \"Value\"")
            .SingleAsync(cancellationToken);
    }

    /// <summary>The folded names of every tag of the kind Person, to compare with the names of people without asking once for each.</summary>
    public static async Task<HashSet<string>> PublicPersonTagNamesAsync(this CommonageDbContext db, CancellationToken cancellationToken = default)
    {
        var kind = (int)TagKind.Person;
        var names = await db.Database
            .SqlQuery<string>($"SELECT lower(commonage_fold(\"Name\")) AS \"Value\" FROM \"Tags\" WHERE \"Kind\" = {kind}")
            .ToListAsync(cancellationToken);
        return names.ToHashSet(StringComparer.Ordinal);
    }

    public enum LinkOutcome
    {
        Linked,
        AlreadyLinked,
        TooMany,
    }

    /// <summary>Links the person to the item. Adds one row; the item is not touched.</summary>
    public static async Task<LinkOutcome> LinkAsync(this CommonageDbContext db, int itemId, Person person, DateTime now, CancellationToken cancellationToken = default)
    {
        if (await db.ItemPeople.AnyAsync(l => l.ArchiveItemId == itemId && l.PersonId == person.Id, cancellationToken))
            return LinkOutcome.AlreadyLinked;
        if (await db.ItemPeople.CountAsync(l => l.ArchiveItemId == itemId, cancellationToken) >= Person.MaxPerItem)
            return LinkOutcome.TooMany;
        db.ItemPeople.Add(new ItemPerson { ArchiveItemId = itemId, Person = person, CreatedAt = now });
        return LinkOutcome.Linked;
    }
}
