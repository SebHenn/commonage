using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Export;
using Commonage.Web.Media;
using Commonage.Web.Pages.Admin.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Commonage.Web.Import;

/// <summary>Why a whole export could not be read at all.</summary>
public enum ExportRefusal
{
    None,
    NotZip,
    NoArchiveJson,
    ArchiveJsonTooLarge,
    NotAnExport,
    VersionNotUnderstood,
    NotReadable,
    TooMany,
    TooMuchData,
}

public enum ExportIssueKind
{
    // Wrong: keeps the item out of the import.
    ValueUnknown,
    ValueInvalid,
    TooLong,
    DateUnreadable,
    RightsUnknown,
    TagTooLong,
    TooManyTags,
    CollectionMissing,
    CollectionAmbiguous,
    TooManyNewCollections,
    FilePathInvalid,
    FileMissing,
    FileUnreadable,
    FileTooLarge,

    // Not wrong, and not imported: the item is left out.
    ItemRejected,
    ItemWithdrawn,
    NoConsentToShare,
    ItemAlreadyThere,
    ItemAlreadyThereDifferentText,
    SameAsEarlierItem,
    AllFilesAlreadyThere,

    // Only worth saying: the item is imported without this, or in spite of it.
    FileMarkedProblem,
    FileChecksumMismatch,
    FileAlreadyThere,
    FileInEarlierItem,
    DateMismatch,
}

public enum ExportIssueSeverity
{
    Problem,
    LeftOut,
    Note,
}

/// <summary><see cref="Field"/> is the name of the property in the export, <see cref="Value"/> what it holds (shortened), for the reader to find it.</summary>
public record ExportIssue(ExportIssueKind Kind, string? Field = null, string? Value = null, string? Detail = null, UploadError? Upload = null)
{
    public ExportIssueSeverity Severity => Kind switch
    {
        <= ExportIssueKind.FileTooLarge => ExportIssueSeverity.Problem,
        <= ExportIssueKind.AllFilesAlreadyThere => ExportIssueSeverity.LeftOut,
        _ => ExportIssueSeverity.Note,
    };
}

public record PlannedExportFile(SourceEntry Entry, ExaminedUpload Examined, string Name, string? AltText, string? Caption, int Rotation);

public record PlannedTag(string Name, TagKind Kind);

public record PlannedNewCollection(string Key, string Title, string? Description);

/// <summary>How many more lines the dry run records, and how many were found beyond that.</summary>
public sealed class IssueBudget(int limit)
{
    private int _remaining = limit;

    public int Omitted { get; set; }

    public bool TryTake() => _remaining > 0 && _remaining-- > 0;
}

public class PlannedExportItem(IssueBudget? budget = null)
{
    private readonly List<ExportIssue> _issues = [];

    public int Number { get; init; }
    public string Title { get; set; } = "";
    public string? Status { get; set; }
    public int? CollectionId { get; set; }
    public string? NewCollectionKey { get; set; }
    public string? CollectionTitle { get; set; }
    public string? CollectionDescription { get; set; }
    public string? Description { get; set; }
    public string? DateText { get; set; }
    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }
    public List<PlannedTag> Tags { get; } = [];
    public string? RightsUri { get; set; }
    public string? RightsHolder { get; set; }
    public string? Credit { get; set; }
    public string? ReferenceNumber { get; set; }
    public string? OriginalLocation { get; set; }
    public bool NeedsTranscription { get; set; }
    public bool HelpWanted { get; set; }
    public MediaLevel MediaLevel { get; set; }
    public DateOnly? PublicFrom { get; set; }
    public IReadOnlyList<ExportIssue> Issues => _issues;
    public List<PlannedExportFile> Files { get; } = [];

    /// <summary>
    /// Keeps the issue while the plan's budget lasts. An item always keeps one issue of the kind that decides about it (a problem,
    /// or a reason to leave it out), so that what is not listed never changes what happens to the item.
    /// </summary>
    public void AddIssue(ExportIssue issue)
    {
        var decides = issue.Severity != ExportIssueSeverity.Note && !_issues.Any(i => i.Severity == issue.Severity);
        if (decides || budget is null || budget.TryTake())
            _issues.Add(issue);
        else
            budget.Omitted++;
    }

    public bool HasProblem => Issues.Any(i => i.Severity == ExportIssueSeverity.Problem);
    public bool IsLeftOut => !HasProblem && Issues.Any(i => i.Severity == ExportIssueSeverity.LeftOut);
    public bool IsReady => !HasProblem && !IsLeftOut;
}

public class ExportPlan
{
    public ExportRefusal Refusal { get; init; }
    public int? FoundVersion { get; init; }
    public int Version { get; init; }
    public IReadOnlyList<PlannedExportItem> Items { get; init; } = [];
    public IReadOnlyList<PlannedNewCollection> NewCollections { get; init; } = [];

    /// <summary>Lines found beyond <see cref="ExportImport.MaxIssues"/>, which are counted and not listed.</summary>
    public int IssuesNotListed { get; init; }

    public IEnumerable<PlannedExportItem> Ready => Items.Where(i => i.IsReady);
    public int ReadyFileCount => Ready.Sum(i => i.Files.Count);
    public long ReadyBytes => Ready.SelectMany(i => i.Files).Sum(f => f.Entry.Length);
    public bool CanImport => Refusal == ExportRefusal.None && Ready.Any();
}

/// <summary><see cref="ItemsNotTried"/> are the items left when the run stopped early; <see cref="ItemsFailed"/> those that could not be created.</summary>
public record ExportImportResult(int Items, int Files, int FilesFailed, int CollectionsCreated, int ItemsFailed = 0, int ItemsNotTried = 0);

/// <summary>
/// Brings the ZIP file of Commonage's own export into the archive as drafts. <see cref="PlanAsync"/> only looks and changes
/// nothing, so it is also the dry run. What of the export is read and what is left out, and why, is in
/// <see cref="ExportImportTable"/>. This class only ever adds; nothing that is there is changed or taken away.
/// </summary>
public partial class ExportImport(
    CommonageDbContext db,
    MediaUploadService uploads,
    AuditLog audit,
    IStringLocalizer<SharedResource> localizer,
    IOptions<UploadOptions> uploadOptions,
    TimeProvider clock,
    IHostApplicationLifetime lifetime,
    ILogger<ExportImport> logger)
{
    public const int MinVersion = 4;

    /// <summary>A run stops after this many failures in a row: a full disk or a lost database fails every file, and each item would only become an empty draft.</summary>
    public const int StopAfterFailures = 5;
    public const int MaxItems = 20_000;
    public const int MaxCollections = 5_000;
    public const int MaxTags = 50_000;
    public const int MaxFilesPerItem = 500;

    /// <summary>Files in all of archive.json, so that thousands of items with hundreds of files each cannot become millions of objects.</summary>
    public const int MaxTotalFiles = 100_000;

    /// <summary>Lines of the dry run that are kept. What is found beyond this is only counted; each item keeps what decides about it.</summary>
    public const int MaxIssues = 10_000;
    public const int MaxTagsPerItemRead = 200;

    /// <summary>A run creates at most this many collections, so a file full of invented ones cannot flood the list.</summary>
    public const int MaxNewCollections = 50;

    public const long MaxArchiveJsonBytes = 256L * 1024 * 1024;
    public const long MaxUncompressedBytes = 8L * 1024 * 1024 * 1024;
    public const string ArchiveJsonPath = "archive.json";
    public const int AltTextMax = 1000;
    private const int MaxDepth = 24;

    [GeneratedRegex(@"^files/(?<item>[0-9]{1,9})/(?<file>[0-9]{1,9})-[^/]{1,300}\z", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex FilePathPattern();

    private static readonly int[] Rotations = [0, 90, 180, 270];

    public async Task<ExportPlan> PlanAsync(IImportSource source, CancellationToken cancellationToken)
    {
        if (source.Entries.Sum(e => e.Length) > MaxUncompressedBytes)
            return Refuse(ExportRefusal.TooMuchData);
        var jsonEntry = source.Entries.FirstOrDefault(e => e.Path == ArchiveJsonPath);
        if (jsonEntry is null)
            return Refuse(ExportRefusal.NoArchiveJson);

        ImportedArchive? archive;
        var limits = new ReadLimits();
        try
        {
            await using var stream = await source.OpenSeekableAsync(jsonEntry, MaxArchiveJsonBytes, cancellationToken);
            var head = new byte[16384];
            var read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
            var (format, version) = Sniff(head.AsSpan(0, read), final: read < head.Length);
            if (format != ArchiveExporter.Format)
                return Refuse(ExportRefusal.NotAnExport);
            if (version is null or < MinVersion or > ArchiveExporter.Version)
                return Refuse(ExportRefusal.VersionNotUnderstood, version);

            stream.Position = 0;
            archive = await JsonSerializer.DeserializeAsync<ImportedArchive>(stream, ReadOptions(limits), cancellationToken);
        }
        catch (EntryTooLargeException)
        {
            return Refuse(ExportRefusal.ArchiveJsonTooLarge);
        }
        catch (JsonException)
        {
            return Refuse(limits.TooMany ? ExportRefusal.TooMany : ExportRefusal.NotReadable);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return Refuse(ExportRefusal.NotReadable);
        }

        if (archive is null)
            return Refuse(ExportRefusal.NotReadable);

        var entriesByPath = source.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var exportedCollections = new Dictionary<int, ImportedCollection>();
        foreach (var collection in archive.Collections ?? [])
        {
            if (collection?.Id is { } collectionId)
                exportedCollections.TryAdd(collectionId, collection);
        }

        var tagKinds = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var tag in archive.Tags ?? [])
        {
            if (Text(tag?.Name) is { } tagName)
                tagKinds.TryAdd(Tag.Normalize(tagName), tag!.Kind);
        }

        var existingCollections = (await db.Collections.AsNoTracking().Select(c => new { c.Id, c.Title }).ToListAsync(cancellationToken))
            .GroupBy(c => Key(c.Title), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList(), StringComparer.OrdinalIgnoreCase);
        var existingItems = (await db.ArchiveItems.AsNoTracking()
                .Where(i => i.Title != "")
                .Select(i => new { i.Id, i.CollectionId, i.Title, i.DateText })
                .ToListAsync(cancellationToken))
            .GroupBy(i => ItemKey(i.CollectionId.ToString(CultureInfo.InvariantCulture), i.Title, i.DateText), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(i => i.Id).ToList(), StringComparer.OrdinalIgnoreCase);

        var items = new List<PlannedExportItem>();
        var earlierItemOfKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var earlierItemOfFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var usedPaths = new HashSet<string>(StringComparer.Ordinal);
        var newCollections = new Dictionary<string, PlannedNewCollection>(StringComparer.OrdinalIgnoreCase);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        long bytesRead = 0;
        var number = 0;
        var budget = new IssueBudget(MaxIssues);

        foreach (var exported in archive.Items ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            number++;
            var item = exported ?? new ImportedItem();
            var planned = new PlannedExportItem(budget) { Number = number, Status = Shorten(item.Status) };
            planned.Title = Shorten(Text(item.Title)) ?? "";
            items.Add(planned);

            // What decides whether the item is imported at all is looked at first; nothing else of a left-out item is examined.
            if (!TryEnum(item.Status, out ItemStatus status))
                planned.AddIssue(new ExportIssue(ExportIssueKind.ValueUnknown, "status", Shorten(item.Status)));
            else if (status == ItemStatus.Rejected)
                planned.AddIssue(new ExportIssue(ExportIssueKind.ItemRejected, "status"));
            else if (status == ItemStatus.Withdrawn)
                planned.AddIssue(new ExportIssue(ExportIssueKind.ItemWithdrawn, "status"));
            // Only a contribution that says yes counts: one without the answer (no key, or null) is left out like a no.
            if (item.Contribution is not null && item.Contribution.ConsentCoversSharing != true)
                planned.AddIssue(new ExportIssue(ExportIssueKind.NoConsentToShare, "contribution"));
            if (planned.Issues.Count > 0)
                continue;

            string? Limited(string field, string? value, int max, ExportIssueKind kind = ExportIssueKind.TooLong)
            {
                var text = Text(value);
                if (text is not null && text.Length > max)
                {
                    Issue(planned, kind, field, Shorten(text), max.ToString(CultureInfo.InvariantCulture));
                    return null;
                }

                return text;
            }

            planned.Title = Limited("title", item.Title, ItemImport.TitleMax) ?? planned.Title;
            planned.Description = Limited("description", item.Description, ItemImport.DescriptionMax);
            planned.RightsHolder = Limited("rightsHolder", item.RightsHolder, ItemImport.HolderMax);
            planned.Credit = Limited("credit", item.Credit, ItemImport.CreditMax);
            planned.ReferenceNumber = Limited("referenceNumber", item.ReferenceNumber, ItemImport.ReferenceMax);
            planned.OriginalLocation = Limited("originalLocation", item.OriginalLocation, ItemImport.LocationMax);
            planned.NeedsTranscription = item.NeedsTranscription == true;
            planned.HelpWanted = item.HelpWanted == true;

            planned.RightsUri = Text(item.RightsUri);
            if (planned.RightsUri is not null && !RightsOptions.IsKnown(planned.RightsUri))
                Issue(planned, ExportIssueKind.RightsUnknown, "rightsUri", Shorten(planned.RightsUri));

            if (item.MediaLevel is null)
                planned.MediaLevel = MediaLevel.Everything;
            else if (TryEnum(item.MediaLevel, out MediaLevel level))
                planned.MediaLevel = level;
            else
                Issue(planned, ExportIssueKind.ValueUnknown, "mediaLevel", Shorten(item.MediaLevel));

            if (item.PublicFrom is not null)
            {
                if (TryDay(item.PublicFrom, out var day) && day.Year >= 1900 && day.Year <= today.Year + 200)
                    planned.PublicFrom = day;
                else
                    Issue(planned, ExportIssueKind.ValueInvalid, "publicFrom", Shorten(item.PublicFrom));
            }

            PlanDate(planned, item.Date);
            PlanTags(planned, item, tagKinds);
            PlanCollection(planned, item, exportedCollections, existingCollections);

            if (!HasProblem(planned))
            {
                var bytes = await PlanFilesAsync(planned, item, source, entriesByPath, usedPaths, earlierItemOfFile, bytesRead, cancellationToken);
                if (bytes < 0)
                    return Refuse(ExportRefusal.TooMuchData);
                bytesRead = bytes;
            }

            if (!HasProblem(planned) && planned.Title.Length > 0 && (planned.CollectionId is not null || planned.NewCollectionKey is not null))
            {
                var collectionKey = planned.CollectionId?.ToString(CultureInfo.InvariantCulture) ?? "new:" + planned.NewCollectionKey;
                var key = ItemKey(collectionKey, planned.Title, planned.DateText);
                if (planned.CollectionId is not null && existingItems.TryGetValue(key, out var sameIds))
                {
                    var theirs = await db.ArchiveItems.AsNoTracking().Where(i => sameIds.Contains(i.Id)).Select(i => i.Description).ToListAsync(cancellationToken);
                    var same = theirs.Any(d => string.Equals(Key(d ?? ""), Key(planned.Description ?? ""), StringComparison.Ordinal));
                    Issue(planned, same ? ExportIssueKind.ItemAlreadyThere : ExportIssueKind.ItemAlreadyThereDifferentText, "title", Shorten(planned.Title));
                }
                else if (!earlierItemOfKey.TryAdd(key, number))
                    Issue(planned, ExportIssueKind.SameAsEarlierItem, "title", Shorten(planned.Title), earlierItemOfKey[key].ToString(CultureInfo.InvariantCulture));
            }

            if (planned.IsReady && planned.NewCollectionKey is { } newKey && !newCollections.ContainsKey(newKey))
            {
                if (newCollections.Count >= MaxNewCollections)
                    Issue(planned, ExportIssueKind.TooManyNewCollections, "collectionId", null, MaxNewCollections.ToString(CultureInfo.InvariantCulture));
                else
                    newCollections[newKey] = new PlannedNewCollection(newKey, planned.CollectionTitle!, planned.CollectionDescription);
            }
        }

        return new ExportPlan { Version = ArchiveExporter.Version, Items = items, NewCollections = newCollections.Values.ToList(), IssuesNotListed = budget.Omitted };
    }

    private static bool HasProblem(PlannedExportItem planned) => planned.Issues.Any(i => i.Severity == ExportIssueSeverity.Problem);

    private static ExportPlan Refuse(ExportRefusal refusal, int? foundVersion = null) => new() { Refusal = refusal, FoundVersion = foundVersion };

    private static void Issue(PlannedExportItem planned, ExportIssueKind kind, string? field = null, string? value = null, string? detail = null, UploadError? upload = null) =>
        planned.AddIssue(new ExportIssue(kind, field, value, detail, upload));

    /// <summary>
    /// The exported earliest and latest day are used when they are real, because the text may be in a vocabulary this version
    /// does not read; the text stays as the display text. Without usable days the text has to be readable.
    /// </summary>
    private static void PlanDate(PlannedExportItem planned, ImportedDate? date)
    {
        var text = Text(date?.Text);
        if (text is null)
            return;
        if (text.Length > DateRange.MaxTextLength)
        {
            Issue(planned, ExportIssueKind.DateUnreadable, "date", Shorten(text));
            return;
        }

        var readable = DateRange.TryParse(text, out var parsed);
        if (TryDay(date!.From, out var from) && TryDay(date.To, out var to) && from <= to)
        {
            planned.DateText = text;
            planned.DateFrom = from;
            planned.DateTo = to;
            if (readable && (parsed.From != from || parsed.To != to))
                Issue(planned, ExportIssueKind.DateMismatch, "date", Shorten(text));
        }
        else if (readable)
        {
            planned.DateText = text;
            planned.DateFrom = parsed.From;
            planned.DateTo = parsed.To;
        }
        else
        {
            Issue(planned, ExportIssueKind.DateUnreadable, "date", Shorten(text));
        }
    }

    private static void PlanTags(PlannedExportItem planned, ImportedItem item, Dictionary<string, string?> tagKinds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in item.Tags ?? [])
        {
            if (Text(raw) is not { } name)
                continue;
            if (name.Length > Tag.MaxLength)
            {
                Issue(planned, ExportIssueKind.TagTooLong, "tags", Shorten(name), Tag.MaxLength.ToString(CultureInfo.InvariantCulture));
                continue;
            }

            var normalized = Tag.Normalize(name);
            if (!seen.Add(normalized))
                continue;
            var kind = TagKind.Topic;
            if (tagKinds.TryGetValue(normalized, out var written) && written is not null && !TryEnum(written, out kind))
            {
                Issue(planned, ExportIssueKind.ValueUnknown, "tags", Shorten(name));
                continue;
            }

            planned.Tags.Add(new PlannedTag(name, kind));
        }

        if (planned.Tags.Count > ItemFields.MaxTagsPerItem)
            Issue(planned, ExportIssueKind.TooManyTags, "tags", null, ItemFields.MaxTagsPerItem.ToString(CultureInfo.InvariantCulture));
    }

    private static void PlanCollection(
        PlannedExportItem planned, ImportedItem item, Dictionary<int, ImportedCollection> exported, Dictionary<string, List<int>> existing)
    {
        if (item.CollectionId is not { } id || !exported.TryGetValue(id, out var collection) || Text(collection.Title) is not { } title)
        {
            Issue(planned, ExportIssueKind.CollectionMissing, "collectionId");
            return;
        }

        if (title.Length > ItemImport.TitleMax)
        {
            Issue(planned, ExportIssueKind.TooLong, "collection", Shorten(title), ItemImport.TitleMax.ToString(CultureInfo.InvariantCulture));
            return;
        }

        planned.CollectionTitle = title;
        if (existing.TryGetValue(Key(title), out var matches))
        {
            if (matches.Count == 1)
                planned.CollectionId = matches[0];
            else
                Issue(planned, ExportIssueKind.CollectionAmbiguous, "collection", Shorten(title));
            return;
        }

        var description = Text(collection.Description);
        if (description is not null && description.Length > ItemImport.DescriptionMax)
        {
            Issue(planned, ExportIssueKind.TooLong, "collection", Shorten(title), ItemImport.DescriptionMax.ToString(CultureInfo.InvariantCulture));
            return;
        }

        planned.NewCollectionKey = Key(title);
        planned.CollectionDescription = description;
    }

    /// <summary>
    /// Looks at each file of the item. Returns the bytes read so far including this item's, or -1 when the total budget is used up.
    /// A path is only a key into the ZIP file; it must have the form the export writes and belong to this item and this file.
    /// </summary>
    private async Task<long> PlanFilesAsync(
        PlannedExportItem planned, ImportedItem item, IImportSource source, Dictionary<string, SourceEntry> entries, HashSet<string> usedPaths,
        Dictionary<string, int> earlierItemOfFile, long bytesRead, CancellationToken cancellationToken)
    {
        var files = (item.Files ?? []).OfType<ImportedFile>()
            .OrderBy(f => f.Position ?? int.MaxValue).ThenBy(f => f.Id ?? int.MaxValue).ToList();
        var alreadyThere = 0;
        var seen = 0;
        foreach (var file in files)
        {
            var shown = Shorten(Text(file.OriginalFileName) ?? "");
            if (!string.IsNullOrEmpty(file.Problem))
            {
                Issue(planned, ExportIssueKind.FileMarkedProblem, "files", shown);
                continue;
            }

            var match = file.Path is null ? null : FilePathPattern().Match(file.Path);
            if (match is not { Success: true }
                || file.Id is not { } fileId || item.Id is not { } itemId
                || match.Groups["item"].Value != itemId.ToString(CultureInfo.InvariantCulture)
                || match.Groups["file"].Value != fileId.ToString(CultureInfo.InvariantCulture)
                || !usedPaths.Add(file.Path!))
            {
                Issue(planned, ExportIssueKind.FilePathInvalid, "files", shown);
                continue;
            }

            if (file.Rotation is { } rotation && !Rotations.Contains(rotation))
            {
                Issue(planned, ExportIssueKind.ValueInvalid, "rotation", shown);
                continue;
            }

            var altText = Text(file.AltText);
            var caption = Text(file.Caption);
            if (altText is { Length: > AltTextMax } || caption is { Length: > MediaFile.MaxCaptionLength })
            {
                Issue(planned, ExportIssueKind.TooLong, "files", shown, (altText is { Length: > AltTextMax } ? AltTextMax : MediaFile.MaxCaptionLength).ToString(CultureInfo.InvariantCulture));
                continue;
            }

            if (!entries.TryGetValue(file.Path!, out var entry))
            {
                Issue(planned, ExportIssueKind.FileMissing, "files", shown);
                continue;
            }

            ExaminedUpload examined;
            try
            {
                await using var content = await source.OpenSeekableAsync(entry, uploadOptions.Value.MaxFileSizeBytes, cancellationToken);
                bytesRead += content.Length;
                if (bytesRead > MaxUncompressedBytes)
                    return -1;
                examined = await uploads.ExamineAsync(content, content.Length, documents: true, cancellationToken);
            }
            catch (EntryTooLargeException)
            {
                Issue(planned, ExportIssueKind.FileTooLarge, "files", shown);
                continue;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                Issue(planned, ExportIssueKind.FileUnreadable, "files", shown, upload: UploadError.UnsupportedFormat);
                continue;
            }

            if (examined.Error != UploadError.None)
            {
                Issue(planned, ExportIssueKind.FileUnreadable, "files", shown, upload: examined.Error);
                continue;
            }

            seen++;
            if (!string.Equals(examined.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Issue(planned, ExportIssueKind.FileChecksumMismatch, "files", shown);
                continue;
            }

            if (earlierItemOfFile.TryGetValue(examined.Sha256!, out var earlier))
            {
                Issue(planned, ExportIssueKind.FileInEarlierItem, "files", shown, earlier.ToString(CultureInfo.InvariantCulture));
                continue;
            }

            if (await uploads.FindIdenticalAsync(examined, cancellationToken: cancellationToken) is { } same)
            {
                alreadyThere++;
                Issue(planned, ExportIssueKind.FileAlreadyThere, "files", shown, ArchiveItem.ShownTitle(localizer, same.ItemTitle));
                continue;
            }

            earlierItemOfFile[examined.Sha256!] = planned.Number;
            planned.Files.Add(new PlannedExportFile(entry, examined, Text(file.OriginalFileName) ?? entry.Name, altText, caption, file.Rotation ?? 0));
        }

        // Every photo of the item is in the archive already: the item is, too, whatever its title says now.
        if (alreadyThere > 0 && planned.Files.Count == 0 && !HasProblem(planned) && alreadyThere == seen)
            Issue(planned, ExportIssueKind.AllFilesAlreadyThere, "files");
        return bytesRead;
    }

    // Titles, tags and collections are compared in NFC and ordinally ignoring case, so that a title written on a Mac (decomposed)
    // and the server's culture make no difference.
    private static string Key(string text) => text.Normalize(NormalizationForm.FormC).Trim();

    private static string ItemKey(string collection, string title, string? date) => $"{collection}\u001f{Key(title)}\u001f{Key(date ?? "")}";

    /// <summary>Trimmed, in NFC, and null when there is nothing in it.</summary>
    private static string? Text(string? value) => ItemFields.Clean(value)?.Normalize(NormalizationForm.FormC);

    private static string? Shorten(string? value) => value is null ? null : value.Length <= 80 ? value : value[..80] + "…";

    /// <summary>A name of the enum, written as such. A number, or a name with other letters, is not one.</summary>
    private static bool TryEnum<T>(string? value, out T result) where T : struct, Enum
    {
        result = default;
        return value is not null && !char.IsDigit(value.FirstOrDefault()) && Enum.TryParse(value, ignoreCase: false, out result) && Enum.IsDefined(result);
    }

    private static bool TryDay(string? value, out DateOnly day)
    {
        day = default;
        return value is not null && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    /// <summary>The format and version, which the exporter writes first, found without reading the rest.</summary>
    private static (string? Format, int? Version) Sniff(ReadOnlySpan<byte> bytes, bool final)
    {
        string? format = null;
        int? version = null;
        if (bytes.StartsWith("﻿"u8.ToArray()) || bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            bytes = bytes[3..];
        try
        {
            var reader = new Utf8JsonReader(bytes, final, default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return (null, null);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                if (!reader.Read())
                    break;
                if (name == "format" && reader.TokenType == JsonTokenType.String)
                    format = reader.GetString();
                else if (name == "version" && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
                    version = number;
                else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && !reader.TrySkip())
                    break;
                if (format is not null && version is not null)
                    break;
            }
        }
        catch (JsonException)
        {
        }

        return (format, version);
    }

    private static JsonSerializerOptions ReadOptions(ReadLimits limits) => new()
    {
        MaxDepth = MaxDepth,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new CappedListConverter<ImportedItem>(MaxItems, limits),
            new CappedListConverter<ImportedCollection>(MaxCollections, limits),
            new CappedListConverter<ImportedTag>(MaxTags, limits),
            new CappedListConverter<ImportedFile?>(MaxFilesPerItem, limits, MaxTotalFiles),
            new CappedListConverter<string?>(MaxTagsPerItemRead, limits),
        },
    };

    /// <summary>
    /// Imports the items of a plan that are in order, as drafts. A file that is no longer what the dry run saw, or that cannot be
    /// stored, is left out and counted; its item stays a draft with the photos that did arrive. Within one item the run is not
    /// cut off by a token, so that it never stops between an item and its photos; between items it stops when the app is shutting
    /// down, and after <see cref="StopAfterFailures"/> failures in a row (a full disk or a lost database), so that it does not go
    /// on creating empty drafts. What was done is in the activity log whichever way the run ends.
    /// </summary>
    public async Task<ExportImportResult> RunAsync(ExportPlan plan, IImportSource source, CancellationToken cancellationToken)
    {
        // The entry is added before the first save, so an import that stops half way still has its line in the log.
        var logged = audit.Record(AuditAction.ArchiveImported, null);
        var now = clock.GetUtcNow().UtcDateTime;
        var created = new Dictionary<string, Collection>(StringComparer.OrdinalIgnoreCase);
        var position = (await db.Collections.MaxAsync(c => (int?)c.Position, cancellationToken) ?? -1) + 1;
        foreach (var planned in plan.NewCollections)
        {
            var collection = new Collection { Title = planned.Title, Description = planned.Description, CreatedAt = now, Position = position++ };
            created[planned.Key] = collection;
            db.Collections.Add(collection);
            audit.Record(AuditAction.CollectionCreatedByImport, planned.Title);
        }

        await db.SaveChangesAsync(cancellationToken);
        var loggedId = logged.Id;
        var newCollectionIds = created.ToDictionary(c => c.Key, c => c.Value.Id, StringComparer.OrdinalIgnoreCase);

        var ready = plan.Ready.ToList();
        var tried = 0;
        var items = 0;
        var files = 0;
        var failed = 0;
        var itemsFailed = 0;
        var streak = 0;
        var tags = new Dictionary<string, Tag>(StringComparer.Ordinal);
        Exception? error = null;
        try
        {
            foreach (var row in ready)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (lifetime.ApplicationStopping.IsCancellationRequested || streak >= StopAfterFailures)
                    break;
                tried++;
                // Everything tracked is looked after at every save, which gets slow with thousands of items.
                if (tried > 1 && tried % 100 == 1)
                {
                    db.ChangeTracker.Clear();
                    tags.Clear();
                }

                var stamp = clock.GetUtcNow().UtcDateTime;
                var item = new ArchiveItem
                {
                    Title = row.Title,
                    Description = row.Description,
                    Status = ItemStatus.Draft,
                    MediaLevel = row.MediaLevel,
                    PublicFrom = row.PublicFrom,
                    CollectionId = row.CollectionId ?? newCollectionIds[row.NewCollectionKey!],
                    RightsUri = row.RightsUri,
                    RightsHolder = row.RightsHolder,
                    Credit = row.Credit,
                    ReferenceNumber = row.ReferenceNumber,
                    OriginalLocation = row.OriginalLocation,
                    NeedsTranscription = row.NeedsTranscription,
                    HelpWanted = row.HelpWanted,
                    CreatedAt = stamp,
                    UpdatedAt = stamp,
                };
                if (row.DateText is not null)
                    item.SetDateRange(row.DateText, row.DateFrom!.Value, row.DateTo!.Value);

                try
                {
                    var missing = row.Tags.Where(t => !tags.ContainsKey(Tag.Normalize(t.Name))).ToList();
                    if (missing.Count > 0)
                    {
                        var normalized = missing.Select(t => Tag.Normalize(t.Name)).ToList();
                        foreach (var found in await db.Tags.Where(t => normalized.Contains(t.NormalizedName)).ToListAsync(cancellationToken))
                            tags[found.NormalizedName] = found;
                        // A tag that is there keeps its kind: the archive's own choice wins over what an export says.
                        foreach (var tag in missing.Where(t => !tags.ContainsKey(Tag.Normalize(t.Name))))
                            tags[Tag.Normalize(tag.Name)] = new Tag { Name = tag.Name, NormalizedName = Tag.Normalize(tag.Name), Kind = tag.Kind };
                    }

                    item.SetTags(row.Tags.Select(t => tags[Tag.Normalize(t.Name)]).ToList());

                    db.ArchiveItems.Add(item);
                    audit.Record(AuditAction.ItemCreated, item.Title, item);
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (Exception exception) when (IsStoreFailure(exception))
                {
                    logger.LogWarning("The import could not create item {Number}: {ExceptionType}", row.Number, exception.GetType().Name);
                    // What the failed save left tracked would fail the next one, too. The run's own line is read again at the end.
                    db.ChangeTracker.Clear();
                    tags.Clear();
                    itemsFailed++;
                    streak++;
                    continue;
                }

                items++;

                var added = 0;
                var failedHere = 0;
                Exception? itemError = null;
                try
                {
                    foreach (var planned in row.Files)
                    {
                        try
                        {
                            await using var content = await source.OpenSeekableAsync(planned.Entry, uploadOptions.Value.MaxFileSizeBytes, cancellationToken);
                            var examined = await uploads.ExamineAsync(content, content.Length, documents: true, cancellationToken);
                            if (examined.Error != UploadError.None || examined.Sha256 != planned.Examined.Sha256)
                            {
                                failed++;
                                failedHere++;
                                continue;
                            }

                            await uploads.StoreAsync(item, content, planned.Name, content.Length, examined, cancellationToken, planned.AltText, planned.Caption, planned.Rotation);
                            files++;
                            added++;
                            streak = 0;
                        }
                        catch (Exception exception) when (IsStoreFailure(exception) || exception is InvalidDataException or EntryTooLargeException)
                        {
                            // Whatever went wrong with one file (a full disk, a lost connection, a broken ZIP entry) costs that file only:
                            // it is counted, and what was thrown is never shown, because it can hold paths. Anything else stops the run.
                            DiscardUnsaved(item);
                            logger.LogWarning("The import could not add a file to item {Number}: {ExceptionType}", row.Number, exception.GetType().Name);
                            failed++;
                            failedHere++;
                            if (IsStoreFailure(exception) && ++streak >= StopAfterFailures)
                                break;
                        }
                        catch
                        {
                            DiscardUnsaved(item);
                            throw;
                        }
                    }
                }
                catch (Exception exception)
                {
                    itemError = exception;
                    throw;
                }
                finally
                {
                    // Also when the loop is cut short: the photos that did arrive, and the ones that did not, are in the log.
                    try
                    {
                        if (added > 0)
                            audit.Record(AuditAction.PhotosAdded, item.Title, item);
                        if (failedHere > 0)
                            audit.Record(AuditAction.ImportPhotosMissing, item.Title, item);
                        if (added > 0 || failedHere > 0)
                            await db.SaveChangesAsync(CancellationToken.None);
                    }
                    catch (Exception exception) when (itemError is not null || IsStoreFailure(exception))
                    {
                        // This must not hide what stopped the loop, and a database that is gone is the same failure as any other.
                        logger.LogWarning("The import could not write the log lines of item {Number}: {ExceptionType}", row.Number, exception.GetType().Name);
                        db.ChangeTracker.Clear();
                        tags.Clear();
                        streak++;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            error = exception;
            throw;
        }
        finally
        {
            // How many came in, as numbers only, so the line reads the same in every language. Written whichever way the run ends.
            try
            {
                db.ChangeTracker.Clear();
                var line = await db.AuditEntries.FirstAsync(e => e.Id == loggedId, CancellationToken.None);
                line.Subject = $"{items} / {files}";
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception exception) when (error is not null)
            {
                logger.LogWarning("The import could not write its summary line: {ExceptionType}", exception.GetType().Name);
            }
        }

        return new ExportImportResult(items, files, failed, created.Count, itemsFailed, ready.Count - tried);
    }

    /// <summary>What a full disk, a broken file system or a lost database throws. Anything else is not something the run can go on after.</summary>
    private static bool IsStoreFailure(Exception exception) => exception is IOException or DbUpdateException or NpgsqlException;

    /// <summary>Forgets a photo that was never saved, so that the next save does not try it again and fail with it.</summary>
    private void DiscardUnsaved(ArchiveItem item)
    {
        foreach (var entry in db.ChangeTracker.Entries<MediaFile>().Where(e => e.State is EntityState.Added or EntityState.Modified).ToList())
            entry.State = entry.State == EntityState.Added ? EntityState.Detached : EntityState.Unchanged;
        item.MediaFiles.RemoveAll(f => f.Id == 0);
    }

    /// <summary>The message for a refused export, in the reader's language.</summary>
    public static string Describe(IStringLocalizer localizer, ExportRefusal refusal, int? foundVersion) => refusal switch
    {
        ExportRefusal.NotZip => localizer["This is not a ZIP file, or it holds too many files."],
        ExportRefusal.NoArchiveJson => localizer["This ZIP file is not a Commonage export: it has no archive.json."],
        ExportRefusal.ArchiveJsonTooLarge => localizer["The file archive.json in this export is too large to read."],
        ExportRefusal.NotAnExport => localizer["This is not a Commonage export."],
        ExportRefusal.VersionNotUnderstood =>
            localizer["This export has the format version {0}. This archive reads the versions {1} to {2}.", foundVersion?.ToString(CultureInfo.InvariantCulture) ?? "?", MinVersion, ArchiveExporter.Version],
        ExportRefusal.NotReadable => localizer["The file archive.json in this export could not be read."],
        ExportRefusal.TooMany => localizer["This export holds more items, files, tags or collections than one import can read."],
        ExportRefusal.TooMuchData => localizer["This export holds more data than one import can read."],
        _ => "",
    };

    /// <summary>The message for one thing found in an item. The field and the value are shown beside it, never built into it.</summary>
    public static string Describe(IStringLocalizer localizer, ExportIssue issue) => issue.Kind switch
    {
        ExportIssueKind.ValueUnknown => localizer["This value is not one this archive knows."],
        ExportIssueKind.ValueInvalid => localizer["This value is not valid."],
        ExportIssueKind.TooLong => localizer.Plural("This text can be at most {0, plural, one {# character} other {# characters}} long.", int.Parse(issue.Detail!, CultureInfo.InvariantCulture)),
        ExportIssueKind.DateUnreadable => localizer["This date could not be understood. Please include a year, for example: 1953, 12.03.1953, circa 1950, 1950s, 1950–1955."],
        ExportIssueKind.RightsUnknown => localizer["This is not one of the archive's rights statements. Write its web address as an export does, or leave the cell empty."],
        ExportIssueKind.TagTooLong => localizer.Plural("A tag can be at most {0, plural, one {# character} other {# characters}} long.", int.Parse(issue.Detail!, CultureInfo.InvariantCulture)),
        ExportIssueKind.TooManyTags => localizer.Plural("An item can have at most {0, plural, one {# tag} other {# tags}}.", int.Parse(issue.Detail!, CultureInfo.InvariantCulture)),
        ExportIssueKind.CollectionMissing => localizer["The collection of this item is not in the export."],
        ExportIssueKind.CollectionAmbiguous => localizer["More than one collection has this name. Rename one of them first."],
        ExportIssueKind.TooManyNewCollections => localizer.Plural("One import creates at most {0, plural, one {# collection} other {# collections}}. Import again for the rest.", int.Parse(issue.Detail!, CultureInfo.InvariantCulture)),
        ExportIssueKind.FilePathInvalid => localizer["The place of this file in the ZIP file is not the one an export writes."],
        ExportIssueKind.FileMissing => localizer["This file is not in the ZIP file."],
        ExportIssueKind.FileUnreadable => UploadErrorMessages.Describe(localizer, issue.Upload ?? UploadError.UnsupportedFormat, issue.Value ?? ""),
        ExportIssueKind.FileTooLarge => localizer["This file is too large."],
        ExportIssueKind.ItemRejected => localizer["Left out: it was rejected in the other archive."],
        ExportIssueKind.ItemWithdrawn => localizer["Left out: its contributor withdrew their consent in the other archive."],
        ExportIssueKind.NoConsentToShare => localizer["Left out: its contributor did not agree to it being shared further."],
        ExportIssueKind.ItemAlreadyThere => localizer["An item with this title and date is already in this collection. It is not changed, and photos it lacks are not added."],
        ExportIssueKind.ItemAlreadyThereDifferentText => localizer["An item with this title and date is already in this collection, with a different description. It is not changed, and photos it lacks are not added."],
        ExportIssueKind.SameAsEarlierItem => localizer["The same title and date as item {0}, in the same collection.", issue.Detail!],
        ExportIssueKind.AllFilesAlreadyThere => localizer["Every photo of this item is already in the archive."],
        ExportIssueKind.FileMarkedProblem => localizer["The other archive could not export this file, so it is left out."],
        ExportIssueKind.FileChecksumMismatch => localizer["This file does not match its checksum, so it is left out."],
        ExportIssueKind.FileAlreadyThere => localizer["The archive already has this file: {0}", issue.Detail!],
        ExportIssueKind.FileInEarlierItem => localizer["The same file as in item {0}.", issue.Detail!],
        ExportIssueKind.DateMismatch => localizer["The days in the export differ from what this text means here. The days of the export are used."],
        _ => issue.Kind.ToString(),
    };
}
