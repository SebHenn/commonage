using System.Globalization;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Commonage.Web.Pages.Admin.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Commonage.Web.Import;

public enum ImportProblem
{
    NoCollection,
    UnknownCollection,
    AmbiguousCollection,
    TooLong,
    TitleTooLong,
    DescriptionTooLong,
    DateUnreadable,
    RightsUnknown,
    TagTooLong,
    TooManyTags,
    FileMissing,
    FileAmbiguous,
    FileUnreadable,
    FileTooLarge,
}

public enum ImportLeftOut
{
    ItemAlreadyThere,
    SameAsEarlierRow,
    FileAlreadyThere,
    FileInEarlierRow,
}

/// <summary>
/// Something wrong with one cell. A problem keeps its row out of the import; <see cref="LeftOut"/> is for a row that is
/// not wrong but would only be there twice. <see cref="Value"/> is what the cell holds, for the reader to find it.
/// </summary>
public record ImportIssue(string Column, string? Value, ImportProblem? Problem = null, ImportLeftOut? LeftOut = null, string? Detail = null, UploadError? Upload = null);

public record PlannedFile(SourceEntry Entry, ExaminedUpload Examined);

public class PlannedRow
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public int? CollectionId { get; set; }
    public string? Description { get; init; }
    public string? Date { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? RightsUri { get; init; }
    public string? RightsHolder { get; init; }
    public string? Credit { get; init; }
    public string? ReferenceNumber { get; init; }
    public string? OriginalLocation { get; init; }
    public List<ImportIssue> Issues { get; } = [];
    public List<PlannedFile> Files { get; } = [];

    public bool HasProblem => Issues.Any(i => i.Problem is not null);
    public bool IsLeftOut => !HasProblem && Issues.Any(i => i.LeftOut is not null);
    public bool IsReady => Issues.Count == 0;
}

public class ImportPlan
{
    public CsvError Error { get; init; }
    public bool NoUsableColumns { get; init; }
    public bool CollectionColumnMissing { get; init; }
    public IReadOnlyList<string> IgnoredColumns { get; init; } = [];
    public IReadOnlyList<PlannedRow> Rows { get; init; } = [];
    public int FilesNotUsed { get; init; }

    /// <summary>The sheet names files, but there is no ZIP file or folder to find them in.</summary>
    public bool SourceMissing { get; init; }

    public IEnumerable<PlannedRow> Ready => Rows.Where(r => r.IsReady);
    public int ReadyFileCount => Ready.Sum(r => r.Files.Count);
    public long ReadyBytes => Ready.SelectMany(r => r.Files).Sum(f => f.Entry.Length);
    public bool CanImport => Error == CsvError.None && !NoUsableColumns && Ready.Any();
}

public record ImportResult(int Items, int Files, int FilesFailed);

/// <summary>
/// Turns a spreadsheet and a ZIP or folder of scans into drafts. <see cref="PlanAsync"/> only looks and changes nothing, so
/// it is also the dry run; <see cref="RunAsync"/> imports the rows of a plan that are in order.
/// </summary>
public class ItemImport(
    CommonageDbContext db,
    MediaUploadService uploads,
    AuditLog audit,
    IStringLocalizer<SharedResource> localizer,
    IOptions<UploadOptions> uploadOptions,
    TimeProvider clock)
{
    public static readonly string[] Columns =
    [
        "collection", "title", "date", "tags", "description", "rights", "rights_holder", "credit", "reference_number", "original_location", "files",
    ];

    private const int TitleMax = 300, DescriptionMax = 20000, HolderMax = 300, CreditMax = 300, ReferenceMax = 100, LocationMax = 300;

    /// <summary>The template: the columns, in order, and no rows.</summary>
    public static string TemplateCsv() => string.Join(',', Columns) + "\r\n";

    /// <summary>
    /// Exports put an apostrophe before a cell that starts with =, +, - or @, so a spreadsheet does not run it as a formula.
    /// Reading takes exactly that one back off, and nothing else: "'Tis the season" stays as it is.
    /// </summary>
    public static string Unprotect(string cell) =>
        cell.Length >= 2 && cell[0] == '\'' && cell[1] is '=' or '+' or '-' or '@' or '\t' or '\r' ? cell[1..] : cell;

    public async Task<ImportPlan> PlanAsync(byte[] csv, IImportSource? source, int? defaultCollectionId, CancellationToken cancellationToken)
    {
        var read = CsvReader.Read(csv);
        if (read.Table is not { } table)
            return new ImportPlan { Error = read.Error };

        var known = new Dictionary<string, int>();
        var ignored = new List<string>();
        for (var i = 0; i < table.Header.Count; i++)
        {
            var name = table.Header[i].Trim().ToLowerInvariant().Replace(' ', '_');
            if (Columns.Contains(name) && known.TryAdd(name, i))
                continue;
            if (table.Header[i].Length > 0)
                ignored.Add(table.Header[i]);
        }

        if (!known.ContainsKey("title") && !known.ContainsKey("files"))
            return new ImportPlan { NoUsableColumns = true, IgnoredColumns = ignored };

        string? Cell(CsvRow row, string column) =>
            known.TryGetValue(column, out var at) && at < row.Cells.Count ? ItemFields.Clean(Unprotect(row.Cells[at].Trim())) : null;

        var collections = await db.Collections.AsNoTracking().Select(c => new { c.Id, c.Title }).ToListAsync(cancellationToken);
        var filesByPath = source?.Entries.GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);
        var filesByName = source?.Entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.Ordinal);

        var itemKeys = await LoadItemKeysAsync(cancellationToken);
        var rowOfKey = new Dictionary<(int, string, string), int>();
        var rowOfFile = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<PlannedRow>();
        var anyFileNamed = false;

        foreach (var csvRow in table.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var title = Cell(csvRow, "title") ?? "";
            var date = Cell(csvRow, "date");
            var row = new PlannedRow
            {
                Number = csvRow.Number,
                Title = title,
                Description = Cell(csvRow, "description"),
                Date = date,
                Tags = Tag.ParseList(Cell(csvRow, "tags")),
                RightsUri = Cell(csvRow, "rights"),
                RightsHolder = Cell(csvRow, "rights_holder"),
                Credit = Cell(csvRow, "credit"),
                ReferenceNumber = Cell(csvRow, "reference_number"),
                OriginalLocation = Cell(csvRow, "original_location"),
            };

            var collectionName = Cell(csvRow, "collection");
            if (collectionName is null)
            {
                if (defaultCollectionId is { } chosen && collections.Any(c => c.Id == chosen))
                    row.CollectionId = chosen;
                else
                    row.Issues.Add(new ImportIssue("collection", null, ImportProblem.NoCollection));
            }
            else
            {
                var matches = collections.Where(c => string.Equals(c.Title.Trim(), collectionName, StringComparison.CurrentCultureIgnoreCase)).ToList();
                if (matches.Count == 1)
                    row.CollectionId = matches[0].Id;
                else
                    row.Issues.Add(new ImportIssue("collection", Shorten(collectionName), matches.Count == 0 ? ImportProblem.UnknownCollection : ImportProblem.AmbiguousCollection));
            }

            void TooLong(string column, string? value, int max, ImportProblem problem = ImportProblem.TooLong)
            {
                if (value is not null && value.Length > max)
                    row.Issues.Add(new ImportIssue(column, Shorten(value), problem, Detail: max.ToString(CultureInfo.InvariantCulture)));
            }

            TooLong("title", title, TitleMax, ImportProblem.TitleTooLong);
            TooLong("description", row.Description, DescriptionMax, ImportProblem.DescriptionTooLong);
            TooLong("rights_holder", row.RightsHolder, HolderMax);
            TooLong("credit", row.Credit, CreditMax);
            TooLong("reference_number", row.ReferenceNumber, ReferenceMax);
            TooLong("original_location", row.OriginalLocation, LocationMax);

            if (date is not null && (date.Length > DateRange.MaxTextLength || !DateRange.TryParse(date, out _)))
                row.Issues.Add(new ImportIssue("date", Shorten(date), ImportProblem.DateUnreadable));
            if (row.RightsUri is not null && !RightsOptions.IsKnown(row.RightsUri))
                row.Issues.Add(new ImportIssue("rights", Shorten(row.RightsUri), ImportProblem.RightsUnknown));
            if (row.Tags.FirstOrDefault(t => t.Length > Tag.MaxLength) is { } longTag)
                row.Issues.Add(new ImportIssue("tags", Shorten(longTag), ImportProblem.TagTooLong, Detail: Tag.MaxLength.ToString(CultureInfo.InvariantCulture)));
            if (row.Tags.Count > ItemFields.MaxTagsPerItem)
                row.Issues.Add(new ImportIssue("tags", null, ImportProblem.TooManyTags, Detail: ItemFields.MaxTagsPerItem.ToString(CultureInfo.InvariantCulture)));

            if (Cell(csvRow, "files") is { } fileCell)
            {
                anyFileNamed = true;
                foreach (var reference in fileCell.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase))
                    await PlanFileAsync(row, reference, source, filesByPath, filesByName, used, rowOfFile, cancellationToken);
            }

            // Only a row that is otherwise in order is looked at for duplicates, so a problem is not hidden behind one.
            if (!row.HasProblem && row.CollectionId is { } collectionId && title.Length > 0)
            {
                var key = (collectionId, title.ToLowerInvariant(), (date ?? "").ToLowerInvariant());
                if (itemKeys.Contains(key))
                    row.Issues.Add(new ImportIssue("title", Shorten(title), LeftOut: ImportLeftOut.ItemAlreadyThere));
                else if (!rowOfKey.TryAdd(key, row.Number))
                    row.Issues.Add(new ImportIssue("title", Shorten(title), LeftOut: ImportLeftOut.SameAsEarlierRow, Detail: rowOfKey[key].ToString(CultureInfo.InvariantCulture)));
            }

            rows.Add(row);
        }

        return new ImportPlan
        {
            IgnoredColumns = ignored,
            Rows = rows,
            FilesNotUsed = source?.Entries.Count(e => !used.Contains(e.Path)) ?? 0,
            CollectionColumnMissing = !known.ContainsKey("collection"),
            SourceMissing = anyFileNamed && source is null,
        };
    }

    private async Task PlanFileAsync(
        PlannedRow row, string reference, IImportSource? source, Dictionary<string, SourceEntry>? byPath,
        Dictionary<string, List<SourceEntry>>? byName, HashSet<string> used, Dictionary<string, int> rowOfFile, CancellationToken cancellationToken)
    {
        var path = reference.Replace('\\', '/').TrimStart('/');
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];

        SourceEntry? entry = null;
        if (source is not null && byPath!.TryGetValue(path, out var exact))
            entry = exact;
        else if (source is not null && !path.Contains('/') && byName!.TryGetValue(path, out var sameName))
        {
            if (sameName.Count > 1)
            {
                row.Issues.Add(new ImportIssue("files", Shorten(reference), ImportProblem.FileAmbiguous));
                return;
            }

            entry = sameName[0];
        }

        if (entry is null)
        {
            row.Issues.Add(new ImportIssue("files", Shorten(reference), ImportProblem.FileMissing));
            return;
        }

        used.Add(entry.Path);
        if (rowOfFile.TryGetValue(entry.Path, out var earlier) && earlier != row.Number)
        {
            row.Issues.Add(new ImportIssue("files", Shorten(reference), LeftOut: ImportLeftOut.FileInEarlierRow, Detail: earlier.ToString(CultureInfo.InvariantCulture)));
            return;
        }

        ExaminedUpload examined;
        try
        {
            await using var content = await source!.OpenSeekableAsync(entry, uploadOptions.Value.MaxFileSizeBytes, cancellationToken);
            examined = await uploads.ExamineAsync(content, content.Length, cancellationToken);
        }
        catch (EntryTooLargeException)
        {
            row.Issues.Add(new ImportIssue("files", Shorten(reference), ImportProblem.FileTooLarge));
            return;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            row.Issues.Add(new ImportIssue("files", Shorten(reference), ImportProblem.FileUnreadable, Upload: UploadError.UnsupportedFormat));
            return;
        }

        if (examined.Error != UploadError.None)
        {
            row.Issues.Add(new ImportIssue("files", Shorten(reference), ImportProblem.FileUnreadable, Upload: examined.Error));
            return;
        }

        rowOfFile[entry.Path] = row.Number;
        if (await uploads.FindIdenticalAsync(examined, cancellationToken) is { } same)
            row.Issues.Add(new ImportIssue("files", Shorten(reference), LeftOut: ImportLeftOut.FileAlreadyThere, Detail: ArchiveItem.ShownTitle(localizer, same.ItemTitle)));
        else
            row.Files.Add(new PlannedFile(entry, examined));
    }

    /// <summary>Title and date of every item that is not in the bin, per collection, to find what an import would add a second time.</summary>
    private async Task<HashSet<(int, string, string)>> LoadItemKeysAsync(CancellationToken cancellationToken)
    {
        var existing = await db.ArchiveItems.AsNoTracking()
            .Where(i => i.Title != "")
            .Select(i => new { i.CollectionId, i.Title, i.DateText })
            .ToListAsync(cancellationToken);
        return existing.Select(i => (i.CollectionId, i.Title.Trim().ToLowerInvariant(), (i.DateText ?? "").ToLowerInvariant())).ToHashSet();
    }

    private static string Shorten(string value) => value.Length <= 80 ? value : value[..80] + "…";

    /// <summary>Imports the rows that are in order. A file that is no longer what the dry run saw is left out and counted.</summary>
    public async Task<ImportResult> RunAsync(ImportPlan plan, IImportSource? source, string sheetName, CancellationToken cancellationToken)
    {
        var items = 0;
        var files = 0;
        var failed = 0;
        var tags = new Dictionary<string, Tag>(StringComparer.Ordinal);
        foreach (var row in plan.Ready)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Everything tracked is looked after at every save, which gets slow with thousands of items.
            if (items > 0 && items % 100 == 0)
            {
                db.ChangeTracker.Clear();
                tags.Clear();
            }

            var now = clock.GetUtcNow().UtcDateTime;
            var item = new ArchiveItem
            {
                Title = row.Title,
                Description = row.Description,
                Status = ItemStatus.Draft,
                CollectionId = row.CollectionId!.Value,
                RightsUri = row.RightsUri,
                RightsHolder = row.RightsHolder,
                Credit = row.Credit,
                ReferenceNumber = row.ReferenceNumber,
                OriginalLocation = row.OriginalLocation,
                CreatedAt = now,
                UpdatedAt = now,
            };
            item.TrySetDate(row.Date);

            var missing = row.Tags.Where(name => !tags.ContainsKey(Tag.Normalize(name))).ToList();
            foreach (var tag in await db.FindOrCreateTagsAsync(missing, cancellationToken))
                tags[tag.NormalizedName] = tag;
            item.SetTags(row.Tags.Select(name => tags[Tag.Normalize(name)]).ToList());

            db.ArchiveItems.Add(item);
            audit.Record(AuditAction.ItemCreated, item.Title, item);
            await db.SaveChangesAsync(cancellationToken);
            items++;

            var added = 0;
            foreach (var planned in row.Files)
            {
                try
                {
                    await using var content = await source!.OpenSeekableAsync(planned.Entry, uploadOptions.Value.MaxFileSizeBytes, cancellationToken);
                    var examined = await uploads.ExamineAsync(content, content.Length, cancellationToken);
                    if (examined.Error != UploadError.None || examined.Sha256 != planned.Examined.Sha256)
                    {
                        failed++;
                        continue;
                    }

                    await uploads.StoreAsync(item, content, planned.Entry.Name, content.Length, examined, cancellationToken);
                    files++;
                    added++;
                }
                catch (Exception exception) when (exception is EntryTooLargeException or InvalidDataException or IOException)
                {
                    failed++;
                }
            }

            // As when photos are added by hand: one entry for the item, so the Photos filter of the log shows them.
            if (added > 0)
                audit.Record(AuditAction.PhotosAdded, item.Title, item);
        }

        audit.Record(AuditAction.ItemsImported, sheetName);
        await db.SaveChangesAsync(cancellationToken);
        return new ImportResult(items, files, failed);
    }

    /// <summary>The message for a cell, in the reader's language. The column and the value are shown beside it, never built into it.</summary>
    public static string Describe(IStringLocalizer localizer, ImportIssue issue) => issue switch
    {
        { Problem: { } problem } => problem switch
        {
            ImportProblem.NoCollection => localizer["No collection is named, and none was chosen for rows without one."],
            ImportProblem.UnknownCollection => localizer["There is no collection with this name. Create it first, or correct the name."],
            ImportProblem.AmbiguousCollection => localizer["More than one collection has this name. Rename one of them first."],
            ImportProblem.TitleTooLong => localizer["The title can be at most {1} characters long.", "", issue.Detail!],
            ImportProblem.DescriptionTooLong => localizer["The description can be at most {1} characters long.", "", issue.Detail!],
            ImportProblem.TooLong => localizer["This text can be at most {1} characters long.", "", issue.Detail!],
            ImportProblem.DateUnreadable => localizer["This date could not be understood. Please include a year, for example: 1953, 12.03.1953, circa 1950, 1950s, 1950–1955."],
            ImportProblem.RightsUnknown => localizer["This is not one of the archive's rights statements. Write its web address as an export does, or leave the cell empty."],
            ImportProblem.TagTooLong => localizer.Plural("A tag can be at most {0, plural, one {# character} other {# characters}} long.", int.Parse(issue.Detail!, CultureInfo.InvariantCulture)),
            ImportProblem.TooManyTags => localizer.Plural("An item can have at most {0, plural, one {# tag} other {# tags}}.", int.Parse(issue.Detail!, CultureInfo.InvariantCulture)),
            ImportProblem.FileMissing => localizer["This file is not in the ZIP file or the folder."],
            ImportProblem.FileAmbiguous => localizer["Several files have this name. Write the path with its folder."],
            ImportProblem.FileTooLarge => localizer["This file is too large."],
            ImportProblem.FileUnreadable => UploadErrorMessages.Describe(localizer, issue.Upload ?? UploadError.UnsupportedFormat, issue.Value ?? ""),
            _ => problem.ToString(),
        },
        { LeftOut: { } leftOut } => leftOut switch
        {
            ImportLeftOut.ItemAlreadyThere => localizer["An item with this title and date is already in this collection."],
            ImportLeftOut.SameAsEarlierRow => localizer["The same title and date as row {0}, in the same collection.", issue.Detail!],
            ImportLeftOut.FileAlreadyThere => localizer["The archive already has this file: {0}", issue.Detail!],
            ImportLeftOut.FileInEarlierRow => localizer["The same file as in row {0}.", issue.Detail!],
            _ => leftOut.ToString(),
        },
        _ => "",
    };
}
