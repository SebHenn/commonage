using System.Globalization;
using System.Security.Cryptography;

namespace Commonage.Web.Domain;

/// <summary>
/// Who may see a person's name, dates and biography. A person is created <see cref="Hidden"/>; a curator makes the choice for
/// each one, never a default. Only <c>Pages/Admin</c>, <c>Data/Visibility.cs</c> and this file name it.
/// </summary>
public enum PersonDisclosure
{
    /// <summary>Only curators see the person. Nothing about them reaches a visitor.</summary>
    Hidden,

    /// <summary>The person has died: name, dates and biography may be shown.</summary>
    Deceased,

    /// <summary>The person may be living, and a curator chose to show their name and what is written about them. Dates stay private.</summary>
    MayBeShown,
}

/// <summary>What an other name of a person is. Shown to visitors only when the curator marks it so.</summary>
public enum PersonNameKind
{
    BirthName,
    MarriedName,
    Nickname,
    OtherSpelling,
    OtherScript,
    Other,
}

/// <summary>
/// A person who appears in the archive, kept by curators. The row holds a curator's note and the disclosure choice, which no
/// public page may read: public pages go through <c>VisiblePeople</c> and its projections only.
/// </summary>
public class Person
{
    public const int NameMaxLength = 200;
    public const int BiographyMaxLength = 4000;
    public const int NoteMaxLength = 4000;
    public const int PublicIdLength = 12;

    /// <summary>The most other names one person has.</summary>
    public const int MaxNames = 20;

    /// <summary>The most people one item links to, so that one page stays a page.</summary>
    public const int MaxPerItem = 200;

    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    public Person()
    {
        PublicId = NewPublicId();
    }

    public int Id { get; set; }

    /// <summary>The address visitors know the person by: 12 random lower-case characters, made once and never changed. Not the internal <see cref="Id"/>.</summary>
    public string PublicId { get; private set; }

    public string Name { get; set; } = "";

    /// <summary>Name folded for searching, written by the database from <see cref="Name"/>.</summary>
    public string FoldedName { get; private set; } = "";

    /// <summary>What the person is sorted by. Empty: sorted by <see cref="Name"/>.</summary>
    public string? SortName { get; set; }

    public PersonDisclosure Disclosure { get; private set; } = PersonDisclosure.Hidden;
    public DateTime? DisclosureChangedAt { get; private set; }

    public string? BornText { get; private set; }
    public DateOnly? BornFrom { get; private set; }
    public DateOnly? BornTo { get; private set; }
    public string? DiedText { get; private set; }
    public DateOnly? DiedFrom { get; private set; }
    public DateOnly? DiedTo { get; private set; }

    public string? Biography { get; set; }

    /// <summary>For curators only. Never on a public page, in an export for visitors, a feed or a link preview.</summary>
    public string? CuratorNote { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>When the person went to the bin, or null. Query filters hide a person in the bin.</summary>
    public DateTime? DeletedAt { get; private set; }

    /// <summary>Concurrency token (PostgreSQL <c>xmin</c>) for the person's own form. Linking never changes it.</summary>
    public uint Version { get; set; }

    public List<PersonName> Names { get; set; } = [];
    public List<ItemPerson> Links { get; set; } = [];

    /// <summary>True when the person is marked as having died. Other code asks this instead of naming <see cref="PersonDisclosure"/>.</summary>
    public bool HasDied => Disclosure == PersonDisclosure.Deceased;

    public bool IsHidden => Disclosure == PersonDisclosure.Hidden;

    public void SetDisclosure(PersonDisclosure value, DateTime now)
    {
        if (Disclosure == value)
            return;
        Disclosure = value;
        DisclosureChangedAt = now;
        UpdatedAt = now;
    }

    public bool TrySetBorn(string? text)
    {
        if (!TryRead(text, out var read))
            return false;
        (BornText, BornFrom, BornTo) = read;
        return true;
    }

    public bool TrySetDied(string? text)
    {
        if (!TryRead(text, out var read))
            return false;
        (DiedText, DiedFrom, DiedTo) = read;
        return true;
    }

    /// <summary>Sets a date from days already read (an import).</summary>
    public void SetBorn(string text, DateOnly from, DateOnly to) => (BornText, BornFrom, BornTo) = (text, from, to);

    public void SetDied(string text, DateOnly from, DateOnly to) => (DiedText, DiedFrom, DiedTo) = (text, from, to);

    private static bool TryRead(string? text, out (string? Text, DateOnly? From, DateOnly? To) read)
    {
        read = (null, null, null);
        if (string.IsNullOrWhiteSpace(text))
            return true;
        if (!DateRange.TryParse(text, out var range))
            return false;
        read = (text.Trim(), range.From, range.To);
        return true;
    }

    public void MoveToBin(DateTime now) => DeletedAt = now;

    public void RestoreFromBin(DateTime now)
    {
        DeletedAt = null;
        UpdatedAt = now;
    }

    public void Touch(DateTime now) => UpdatedAt = now;

    /// <summary>60 random bits as 12 characters of Crockford's base32, in lower case (no i, l, o or u).</summary>
    public static string NewPublicId()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        var bits = BitConverter.ToUInt64(bytes) & ((1UL << 60) - 1);
        Span<char> chars = stackalloc char[PublicIdLength];
        for (var i = PublicIdLength - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(bits & 31)];
            bits >>= 5;
        }

        return new string(chars);
    }

    /// <summary>A name as it is kept: white space made single, in NFC, and null when nothing is left.</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Normalize(System.Text.NormalizationForm.FormC);
    }

    public static bool IsPublicId(string? value) =>
        value is { Length: PublicIdLength } && value.All(c => Alphabet.Contains(c));

    /// <summary>A language tag as a curator may write it (BCP 47), or null when it is empty. False when it is not a tag.</summary>
    public static bool TryCleanLanguage(string? value, out string? language)
    {
        language = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;
        var trimmed = value.Trim();
        if (trimmed.Length > PersonName.LanguageMaxLength || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            return false;
        try
        {
            var culture = CultureInfo.GetCultureInfo(trimmed, predefinedOnly: false);
            language = culture.Name.Length > 0 ? trimmed : null;
            return language is not null;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}

/// <summary>An other name of a person. Visitors see it only when <see cref="ShownToVisitors"/> is set and the person is visible.</summary>
public class PersonName
{
    public const int LanguageMaxLength = 35;

    public int Id { get; set; }
    public int PersonId { get; set; }
    public Person Person { get; set; } = null!;
    public string Name { get; set; } = "";

    /// <summary>Written by the database from <see cref="Name"/>.</summary>
    public string FoldedName { get; private set; } = "";

    public PersonNameKind Kind { get; set; }

    /// <summary>BCP 47 language tag, or null.</summary>
    public string? Language { get; set; }

    public bool ShownToVisitors { get; set; }
}

/// <summary>A person named in an item. Saved without the item, so the item's version never changes by linking.</summary>
public class ItemPerson
{
    public int ArchiveItemId { get; set; }
    public ArchiveItem ArchiveItem { get; set; } = null!;
    public int PersonId { get; set; }
    public Person Person { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
