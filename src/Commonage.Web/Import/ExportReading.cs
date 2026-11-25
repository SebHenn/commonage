using System.Text.Json;
using System.Text.Json.Serialization;

namespace Commonage.Web.Import;

/// <summary>What the importer does with one property of the export.</summary>
public enum ExportFieldDecision
{
    /// <summary>Read, checked and kept in the archive.</summary>
    Read,

    /// <summary>Read only to decide something, or only to find the way to something else; never kept.</summary>
    ReadForDecision,

    /// <summary>Not read: the property has no place in the classes below, so it is skipped while the file is read.</summary>
    Dropped,
}

public record ExportFieldRule(ExportFieldDecision Decision, string Why);

/// <summary>
/// A decision for every property the export writes: read, read only for a decision, or dropped, each with its reason. A test
/// fails when the exporter gains a property that is not listed here, so a new field is never carried over, or lost, by accident.
/// The classes <see cref="ImportedItem"/> and the like hold exactly the properties that are not dropped.
/// </summary>
public static class ExportImportTable
{
    private const string OtherArchive = "A setting or text of the other archive; a backup restores those, an import does not.";
    private const string Personal = "A contributor's details and consent were given to the other archive for that archive, so they stay there.";
    private const string NotTrusted = "Made again here, or not trusted from outside.";

    public static readonly IReadOnlyDictionary<string, ExportFieldRule> Fields = Build();

    private static Dictionary<string, ExportFieldRule> Build()
    {
        var table = new Dictionary<string, ExportFieldRule>();
        void Add(string type, ExportFieldDecision decision, string why, params string[] names)
        {
            foreach (var name in names)
                table.Add($"{type}.{name}", new ExportFieldRule(decision, why));
        }

        Add("ExportSettings", ExportFieldDecision.Dropped, OtherArchive,
            "DefaultLanguage", "ContentLanguage", "OtherContentLanguages", "AccentColor", "TimeZone", "DefaultRightsUri", "AllowSearchEngines",
            "RequireTwoFactor", "LargePhotos", "PrivacyReviewed", "ImprintNotNeeded", "RestoreTestedAt", "ActivityLogKeptYears",
            "RejectedKeptMonths", "AcceptVisitorNotes", "Logo", "Texts");
        Add("ExportText", ExportFieldDecision.Dropped, OtherArchive, "Language", "Kind", "Text");

        Add("ExportCollection", ExportFieldDecision.ReadForDecision, "Only the key that the items of the export name their collection by.", "Id");
        Add("ExportCollection", ExportFieldDecision.Read, "Collections are matched by title, and created with this when there is none.", "Title", "Description");
        Add("ExportCollection", ExportFieldDecision.Dropped, NotTrusted, "CreatedAt", "Position", "Sort", "CoverFileId");

        Add("ExportTag", ExportFieldDecision.Read, "Tags are matched by name, and created with this kind when there is none.", "Name", "Kind");

        Add("ExportItem", ExportFieldDecision.ReadForDecision, "Only the key that the files of the item are checked against.", "Id");
        Add("ExportItem", ExportFieldDecision.ReadForDecision, "Only says which collection of the export the item is in.", "CollectionId");
        Add("ExportItem", ExportFieldDecision.ReadForDecision,
            "Rejected and withdrawn items are left out, so a withdrawal is not undone. Whatever the status, an item arrives as a draft.", "Status");
        Add("ExportItem", ExportFieldDecision.Read, "Comes with the item.",
            "Title", "Description", "Date", "Tags", "RightsUri", "RightsHolder", "Credit", "ReferenceNumber", "OriginalLocation",
            "NeedsTranscription", "HelpWanted", "Files");
        Add("ExportItem", ExportFieldDecision.Read, "Kept, so that what a curator held back in one archive is not lost by moving it. Shown in the dry run.", "MediaLevel", "PublicFrom");
        Add("ExportItem", ExportFieldDecision.Dropped, "Made again: the item is new here, and a draft.", "CreatedAt", "UpdatedAt", "PublishedAt", "RejectedOrWithdrawnAt");
        Add("ExportItem", ExportFieldDecision.Dropped, "Names or describes who gave the item to the other archive, or what that archive's curators wrote to each other.", "ReceivedFrom", "ReceivedOn", "InternalNotes");
        Add("ExportItem", ExportFieldDecision.ReadForDecision,
            "Only whether the contributor agreed to the item being shared further; an item without that agreement is left out. Nothing else of it is read.", "Contribution");
        Add("ExportItem", ExportFieldDecision.Dropped, "Notes from visitors are for the curators of the archive they were sent to.", "Notes");

        Add("ExportDate", ExportFieldDecision.Read, "The text is kept as written; the earliest and latest day are used when they are sane.", "Text", "From", "To");

        Add("ExportContribution", ExportFieldDecision.ReadForDecision, "Decides whether the item is imported at all.", "ConsentCoversSharing");
        Add("ExportContribution", ExportFieldDecision.Dropped, Personal,
            "ContributorName", "ContributorEmail", "ShowName", "Rights", "RightsHolder", "ConsentText", "Reuse", "ReferenceCode", "SubmittedAt",
            "ReviewedAt", "PersonalDetailsRemovedAt", "ContributionLink");

        Add("ExportNote", ExportFieldDecision.Dropped, "Notes from visitors are for the curators of the archive they were sent to.",
            "Kind", "Message", "Name", "Email", "ReceivedAt", "HandledAt", "PersonalDetailsRemovedAt");

        const string StoryNotImported = "Stories are not imported: they are made of the items of the other archive, and an import makes new items with new numbers. The dry run only counts them.";
        Add("ExportStory", ExportFieldDecision.Dropped, StoryNotImported, "Id", "Title", "Summary", "Status", "CreatedAt", "PublishedAt", "Position", "Blocks");
        Add("ExportStoryBlock", ExportFieldDecision.Dropped, StoryNotImported, "Kind", "Text", "ItemId", "Caption");

        Add("ExportFile", ExportFieldDecision.ReadForDecision, "Only the key that the path of the file is checked against.", "Id");
        Add("ExportFile", ExportFieldDecision.Read, "Comes with the photo; the path is only a key into the ZIP file and never a path on disk.",
            "Path", "OriginalFileName", "Sha256", "AltText", "Caption", "Position", "Rotation");
        Add("ExportFile", ExportFieldDecision.ReadForDecision, "A file the export itself marked as missing or damaged is left out.", "Problem");
        Add("ExportFile", ExportFieldDecision.Dropped, "Found out again from the file itself: what a ZIP file claims about a file is not trusted.", "ContentType", "SizeBytes", "PageCount");
        Add("ExportFile", ExportFieldDecision.Dropped, NotTrusted, "UploadedAt");
        return table;
    }
}

// What is read of archive.json. A property that is not here is skipped by the reader, not stored; ExportImportTable says which
// and why. Strings stay strings until they are checked, so that a value this version does not know is one line in the dry
// run and not an exception.

public sealed class ImportedArchive
{
    public string? Format { get; set; }
    public int? Version { get; set; }
    public List<ImportedCollection>? Collections { get; set; }
    public List<ImportedTag>? Tags { get; set; }
    public List<ImportedItem>? Items { get; set; }

    /// <summary>Only how many stories there are: they are never read, see <see cref="ExportImportTable"/>.</summary>
    public StoryCount? Stories { get; set; }
}

/// <summary>The number of stories of an export. Reading skips every story without making an object of it.</summary>
[JsonConverter(typeof(StoryCountConverter))]
public sealed record StoryCount(int Count);

internal sealed class StoryCountConverter : JsonConverter<StoryCount>
{
    /// <summary>The count stops here; the stories beyond it are skipped all the same.</summary>
    public const int Cap = 1_000_000;

    public override StoryCount? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException();
        var count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            reader.Skip();
            if (count < Cap)
                count++;
        }

        return new StoryCount(count);
    }

    public override void Write(Utf8JsonWriter writer, StoryCount value, JsonSerializerOptions options) => throw new NotSupportedException();
}

public sealed class ImportedCollection
{
    public int? Id { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
}

public sealed class ImportedTag
{
    public string? Name { get; set; }
    public string? Kind { get; set; }
}

public sealed class ImportedItem
{
    public int? Id { get; set; }
    public int? CollectionId { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Status { get; set; }
    public ImportedDate? Date { get; set; }
    public List<string?>? Tags { get; set; }
    public string? RightsUri { get; set; }
    public string? RightsHolder { get; set; }
    public string? Credit { get; set; }
    public string? ReferenceNumber { get; set; }
    public string? OriginalLocation { get; set; }
    public bool? NeedsTranscription { get; set; }
    public bool? HelpWanted { get; set; }
    public string? MediaLevel { get; set; }
    public string? PublicFrom { get; set; }
    public ImportedContribution? Contribution { get; set; }
    public List<ImportedFile?>? Files { get; set; }
}

public sealed class ImportedDate
{
    public string? Text { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
}

/// <summary>The one thing read of a contribution. Its names, email address, consent text and the rest are never read.</summary>
public sealed class ImportedContribution
{
    public bool? ConsentCoversSharing { get; set; }
}

public sealed class ImportedFile
{
    public int? Id { get; set; }
    public string? Path { get; set; }
    public string? OriginalFileName { get; set; }
    public string? Sha256 { get; set; }
    public string? AltText { get; set; }
    public string? Caption { get; set; }
    public int? Position { get; set; }
    public int? Rotation { get; set; }
    public string? Problem { get; set; }
}

/// <summary>What went wrong while counting: tells a list that was too long from a file that is not JSON.</summary>
internal sealed class ReadLimits
{
    public bool TooMany { get; set; }

    /// <summary>Elements read so far of the lists that have a limit for all of them together.</summary>
    public int Counted { get; set; }
}

/// <summary>
/// Reads a JSON array into a list and stops at once when it holds more than <paramref name="max"/> elements, so that a file
/// made of a hundred million empty items never becomes a hundred million objects.
/// </summary>
internal sealed class CappedListConverter<T>(int max, ReadLimits limits, int? totalMax = null) : JsonConverter<List<T>>
{
    public override List<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException();
        var list = new List<T>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (list.Count >= max)
            {
                limits.TooMany = true;
                throw new JsonException();
            }

            if (totalMax is { } total && ++limits.Counted > total)
            {
                limits.TooMany = true;
                throw new JsonException();
            }

            list.Add(JsonSerializer.Deserialize<T>(ref reader, options)!);
        }

        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options) => throw new NotSupportedException();
}
