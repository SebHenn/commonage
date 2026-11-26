using Commonage.Web.Domain;

namespace Commonage.Web.Data;

/// <summary>A day range written as text, for a date of a person that an import brings.</summary>
public record ImportedPersonDate(string Text, DateOnly From, DateOnly To);

public record ImportedPersonOtherName(string Name, PersonNameKind Kind, string? Language);

/// <summary>What an import knows of a person. Who may see them is not in it: that is this archive's decision.</summary>
public record ImportedPersonData(
    string Name, string? SortName, ImportedPersonDate? Born, ImportedPersonDate? Died, string? Biography, IReadOnlyList<ImportedPersonOtherName> OtherNames);

/// <summary>
/// People that arrive with an import. Every one is a new person who only curators see, with every other name off for visitors, and
/// is never matched with a person that is here already: a name that is the same may be another person, and an import must not
/// attach a stranger's biography to someone this archive already knows.
/// </summary>
public static class PeopleImport
{
    public static Person AddImportedPerson(this CommonageDbContext db, ImportedPersonData data, DateTime now)
    {
        var person = db.AddHiddenPerson(data.Name, now);
        person.SortName = data.SortName;
        person.Biography = data.Biography;
        if (data.Born is { } born)
            person.SetBorn(born.Text, born.From, born.To);
        if (data.Died is { } died)
            person.SetDied(died.Text, died.From, died.To);
        foreach (var name in data.OtherNames)
            person.Names.Add(new PersonName { Name = name.Name, Kind = name.Kind, Language = name.Language, ShownToVisitors = false });
        return person;
    }

    /// <summary>Links an item that is about to be saved to people that arrived with the same import.</summary>
    public static void LinkImported(ArchiveItem item, IEnumerable<int> personIds, DateTime now)
    {
        foreach (var id in personIds)
            item.People.Add(new ItemPerson { PersonId = id, CreatedAt = now });
    }

    /// <summary>
    /// A person of the demo archive, linked to an item. Someone who has died is shown to visitors (once the pages for people exist),
    /// with the other names marked for visitors; everyone else stays with the curators only.
    /// </summary>
    public static Person AddDemoPerson(
        this CommonageDbContext db, string name, bool hasDied, string? born, string? died, string? biography, string? note,
        IEnumerable<ImportedPersonOtherName> otherNamesForVisitors, ArchiveItem item, DateTime now)
    {
        var person = db.AddHiddenPerson(name, now);
        person.Biography = biography;
        person.CuratorNote = note;
        if (born is not null && !person.TrySetBorn(born) || died is not null && !person.TrySetDied(died))
            throw new InvalidOperationException("A date of the demo cannot be read.");
        if (hasDied)
            person.SetDisclosure(PersonDisclosure.Deceased, now);
        foreach (var other in otherNamesForVisitors)
            person.Names.Add(new PersonName { Name = other.Name, Kind = other.Kind, Language = other.Language, ShownToVisitors = true });
        item.People.Add(new ItemPerson { Person = person, CreatedAt = now });
        return person;
    }
}
