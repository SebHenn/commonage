using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Media;
using Commonage.Web.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Export;

public record ExportOptions(bool PublishedOnly, bool IncludePersonalData);

/// <summary>
/// Writes the whole archive as one ZIP file that needs no Commonage to read: every original file, all
/// descriptions as JSON (complete) and as CSV (for spreadsheets), and a README that explains the format.
/// The ZIP is written straight to the output, so even a large archive never has to fit in memory or on disk.
/// </summary>
public class ArchiveExporter(
    CommonageDbContext db,
    IFileStorage storage,
    TimeProvider clock,
    IStringLocalizer<SharedResource> localizer,
    ILogger<ArchiveExporter> logger)
{
    public const string Format = "commonage-export";
    public const int Version = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Letters such as ü or 東 are written as they are, so the file stays readable in any text editor.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public string FileName => $"commonage-export-{clock.GetUtcNow():yyyy-MM-dd}.zip";

    /// <summary>How many items are held in memory at a time. Memory use stays the same for 500 items or 500,000.</summary>
    public int BatchSize { get; set; } = 500;

    public async Task WriteAsync(Stream output, ExportOptions options, CancellationToken cancellationToken = default)
    {
        // One snapshot for every query below, so that the files, descriptions and lists agree even when someone
        // changes the archive while a large export is being written. Nothing is written to the database.
        await using var snapshot = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var collections = await db.Collections.AsNoTracking().OrderBy(c => c.Id).ToListAsync(cancellationToken);
        var source = (options.PublishedOnly ? db.PublishedItems : db.ArchiveItems).AsNoTracking();
        if (options.PublishedOnly)
        {
            var used = await source.Select(i => i.CollectionId).Distinct().ToListAsync(cancellationToken);
            collections = collections.Where(c => used.Contains(c.Id)).ToList();
        }

        await using var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8, cancellationToken);

        // Files first, so the descriptions written afterwards can say which files could not be read. Only the problems
        // and the covers are remembered; the descriptions are read again from the database, a batch at a time.
        var problems = new Dictionary<int, string>();
        var coverIds = collections.Where(c => c.CoverMediaFileId is not null).Select(c => c.CoverMediaFileId!.Value).ToHashSet();
        var exportedCovers = new HashSet<int>();
        await foreach (var batch in ItemBatchesAsync(source.Select(i => i.Id), cancellationToken))
        {
            var files = await db.MediaFiles.AsNoTracking()
                .Where(f => batch.Contains(f.ArchiveItemId))
                .OrderBy(f => f.ArchiveItemId).ThenBy(f => f.Position).ThenBy(f => f.Id)
                .ToListAsync(cancellationToken);
            foreach (var file in files)
            {
                var (problem, _) = await WriteFileAsync(zip, file, cancellationToken);
                if (problem is not null)
                    problems[file.Id] = problem;
                if (coverIds.Contains(file.Id))
                    exportedCovers.Add(file.Id);
            }
        }

        var settings = await db.ArchiveSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken) ?? new ArchiveSettings();
        var texts = await db.ArchiveTexts.AsNoTracking().OrderBy(t => t.Language).ThenBy(t => t.Kind).ToListAsync(cancellationToken);
        string? logoPath = null;
        if (settings.LogoId is { } logoId)
        {
            try
            {
                await using var logo = storage.OpenRead(LogoImages.KeyFor(logoId, "logo"));
                logoPath = "branding/logo.png";
                await WriteEntryAsync(zip, logoPath, CompressionLevel.NoCompression, stream => logo.CopyToAsync(stream, cancellationToken), cancellationToken);
            }
            catch (FileNotFoundException)
            {
                logger.LogWarning("The archive's logo was not found.");
            }
        }

        // The tags of what is exported only: an export of published items does not name what only drafts mention.
        var tags = (await source.SelectMany(i => i.Tags).Select(t => new { t.Id, t.Name, t.Kind }).Distinct().ToListAsync(cancellationToken))
            .OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => new ExportTag(t.Name, t.Kind)).ToList();
        var exportedCollections = collections.Select(c => new ExportCollection(
            c.Id, c.Title, c.Description, c.CreatedAt, c.Position, c.Sort,
            // The cover is named by the id of its file, and only when that file is in this export.
            exportedCovers.Contains(c.CoverMediaFileId ?? 0) ? c.CoverMediaFileId : null)).ToList();

        await WriteEntryAsync(zip, "archive.json", CompressionLevel.Optimal, async stream =>
        {
            await using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JsonOptions.Encoder });
            json.WriteStartObject();
            Property(json, "format", Format);
            Property(json, "version", Version);
            Property(json, "exportedAt", clock.GetUtcNow().UtcDateTime);
            Property(json, "publishedOnly", options.PublishedOnly);
            Property(json, "includesPersonalData", options.IncludePersonalData);
            Property(json, "collections", exportedCollections);
            Property(json, "tags", tags);
            json.WritePropertyName("items");
            json.WriteStartArray();
            await foreach (var item in DescribeItemsAsync(source, options, problems, cancellationToken))
            {
                JsonSerializer.Serialize(json, item, JsonOptions);
                if (json.BytesPending > 64 * 1024)
                    await json.FlushAsync(cancellationToken);
            }

            json.WriteEndArray();
            Property(json, "settings", new ExportSettings(
                settings.DefaultLanguage, settings.ContentLanguage, settings.OtherContentLanguages, settings.AccentColor, settings.TimeZone, settings.DefaultRightsUri, settings.AllowSearchEngines, settings.RequireTwoFactor, settings.LargePhotos, settings.PrivacyReviewed,
                settings.ImprintNotNeeded, settings.RestoreTestedAt, settings.ActivityLogKeptYears, settings.RejectedKeptMonths, settings.AcceptVisitorNotes, logoPath,
                texts.Select(t => new ExportText(t.Language, t.Kind, t.Text)).ToList()));
            json.WriteEndObject();
        }, cancellationToken);
        await WriteEntryAsync(zip, "items.csv", CompressionLevel.Optimal,
            stream => ItemsCsv.WriteAsync(stream, exportedCollections.ToDictionary(c => c.Id, c => c.Title), DescribeItemsAsync(source, options, problems, cancellationToken), cancellationToken), cancellationToken);
        var readme = Readme(localizer);
        await WriteEntryAsync(zip, "README.txt", CompressionLevel.Optimal,
            stream => stream.WriteAsync(Encoding.UTF8.GetBytes(readme), cancellationToken).AsTask(), cancellationToken);
        // Whoever opens the copy in years to come may not read the exporter's language.
        var english = EnglishReadme();
        if (english != readme)
            await WriteEntryAsync(zip, "README.en.txt", CompressionLevel.Optimal,
                stream => stream.WriteAsync(Encoding.UTF8.GetBytes(english), cancellationToken).AsTask(), cancellationToken);
    }

    private static void Property<T>(Utf8JsonWriter json, string name, T value)
    {
        json.WritePropertyName(name);
        JsonSerializer.Serialize(json, value, JsonOptions);
    }

    /// <summary>The ids of the items in ascending order, a batch at a time.</summary>
    private async IAsyncEnumerable<List<int>> ItemBatchesAsync(
        IQueryable<int> ids, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var last = 0;
        while (true)
        {
            var after = last;
            var batch = await ids.Where(id => id > after).OrderBy(id => id).Take(BatchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0)
                yield break;
            last = batch[^1];
            yield return batch;
        }
    }

    /// <summary>The items in the order of their ids, read a batch at a time.</summary>
    private async IAsyncEnumerable<ExportItem> DescribeItemsAsync(
        IQueryable<ArchiveItem> source, ExportOptions options, Dictionary<int, string> problems,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var ids in ItemBatchesAsync(source.Select(i => i.Id), cancellationToken))
        {
            var items = await source
                .Where(i => ids.Contains(i.Id))
                .Include(i => i.Tags)
                .Include(i => i.MediaFiles)
                .Include(i => i.Contribution).ThenInclude(c => c!.ContributionLink)
                .AsSplitQuery()
                .OrderBy(i => i.Id)
                .ToListAsync(cancellationToken);
            // Notes from visitors are for curators, so a copy of what is public has none.
            var notes = options.PublishedOnly
                ? null
                : (await db.VisitorNotes.AsNoTracking().Where(n => ids.Contains(n.ArchiveItemId)).OrderBy(n => n.Id).ToListAsync(cancellationToken)).ToLookup(n => n.ArchiveItemId);
            foreach (var item in items)
            {
                var files = item.MediaFiles.OrderBy(f => f.Position).ThenBy(f => f.Id).Select(file =>
                {
                    var problem = problems.GetValueOrDefault(file.Id);
                    return Describe(file, problem == "missing" ? null : PathOf(item.Id, file), problem);
                }).ToList();
                yield return Describe(item, files, notes?[item.Id], options);
            }
        }
    }

    private static string PathOf(int itemId, MediaFile file) => $"files/{itemId}/{file.Id}-{SafeFileName(file.OriginalFileName)}";

    /// <summary>Copies the original into the ZIP and checks it against the checksum taken at upload.</summary>
    private async Task<(string? Problem, string? Path)> WriteFileAsync(ZipArchive zip, MediaFile file, CancellationToken cancellationToken)
    {
        var path = PathOf(file.ArchiveItemId, file);
        string? problem = null;
        Stream source;
        try
        {
            source = storage.OpenRead(file.OriginalKey);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            logger.LogWarning("The original of media file {MediaFileId} is missing from storage.", file.Id);
            return ("missing", null);
        }

        await using (source)
        {
            // Photos are already compressed; compressing them again only costs time.
            var level = file.ContentType is "image/jpeg" or "image/webp" ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
            var entry = zip.CreateEntry(path, level);
            entry.LastWriteTime = file.UploadedAt;
            await using var target = await entry.OpenAsync(cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), file.Sha256, StringComparison.Ordinal))
            {
                logger.LogWarning("The original of media file {MediaFileId} does not match its checksum.", file.Id);
                problem = "checksumMismatch";
            }
        }

        return (problem, path);
    }

    private static ExportFile Describe(MediaFile file, string? path, string? problem) =>
        new(file.Id, path, file.OriginalFileName, file.ContentType, file.SizeBytes, file.Sha256, file.UploadedAt, file.AltText, file.Caption, file.Position, file.Rotation, problem);

    private static ExportItem Describe(ArchiveItem item, List<ExportFile> files, IEnumerable<VisitorNote>? notes, ExportOptions options) => new(
        item.Id,
        item.CollectionId,
        item.Title,
        item.Description,
        item.Status,
        item.DateText is null ? null : new ExportDate(item.DateText, item.DateFrom, item.DateTo),
        item.Tags.Select(t => t.Name).Order(StringComparer.Ordinal).ToList(),
        item.CreatedAt,
        item.UpdatedAt,
        item.PublishedAt,
        item.RejectedOrWithdrawnAt,
        item.RightsUri,
        options.IncludePersonalData ? item.RightsHolder : null,
        options.IncludePersonalData ? item.Credit : null,
        // A copy of what is public leaves out what only curators see.
        options.PublishedOnly ? null : item.ReferenceNumber,
        options.PublishedOnly ? null : item.OriginalLocation,
        options.PublishedOnly || !options.IncludePersonalData ? null : item.ReceivedFrom,
        options.PublishedOnly ? null : item.ReceivedOn,
        options.PublishedOnly || !options.IncludePersonalData ? null : item.InternalNotes,
        options.PublishedOnly ? null : item.NeedsTranscription,
        item.HelpWanted,
        item.Contribution is not { } contribution ? null : new ExportContribution(
            options.IncludePersonalData ? contribution.ContributorName : null,
            options.IncludePersonalData ? contribution.ContributorEmail : null,
            contribution.ShowName,
            contribution.Rights,
            options.IncludePersonalData ? contribution.RightsHolder : null,
            contribution.ConsentText,
            contribution.Reuse,
            contribution.ConsentCoversSharing,
            contribution.ReferenceCode,
            contribution.SubmittedAt,
            contribution.ReviewedAt,
            contribution.PersonalDetailsRemovedAt,
            contribution.ContributionLink?.Title),
        notes?.Select(n => new ExportNote(
            n.Kind,
            // What a visitor wrote may name people, so it is personal data like the curators' internal notes.
            options.IncludePersonalData ? n.Message : null,
            options.IncludePersonalData ? n.Name : null,
            options.IncludePersonalData ? n.Email : null,
            n.ReceivedAt,
            n.HandledAt,
            n.PersonalDetailsRemovedAt)).ToList(),
        files);

    private static async Task WriteEntryAsync(
        ZipArchive zip, string name, CompressionLevel level, Func<Stream, Task> write, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(name, level);
        await using var stream = await entry.OpenAsync(cancellationToken);
        await write(stream);
    }

    /// <summary>A name that is safe as a path inside a ZIP on every operating system. The full original name is in archive.json.</summary>
    internal static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var safe = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (safe.Length == 0)
            safe = "file";
        return safe.Length <= 100 ? safe : safe[..100];
    }

    /// <summary>
    /// The README in the language of whoever exports, so that the people who keep the copy can read it. The names of files
    /// and fields stay English, because they are what the files contain.
    /// </summary>
    internal static string Readme(IStringLocalizer localizer)
    {
        var text = new StringBuilder();
        void Paragraph(string paragraph, string indent = "")
        {
            var line = new StringBuilder(indent);
            foreach (var word in paragraph.Split(' '))
            {
                if (line.Length > indent.Length && line.Length + 1 + word.Length > 76)
                {
                    text.Append(line.ToString().TrimEnd()).Append('\n');
                    line.Clear().Append(indent);
                }
                line.Append(word).Append(' ');
            }
            text.Append(line.ToString().TrimEnd()).Append("\n\n");
        }
        void Entry(string name, string description)
        {
            text.Append(name).Append('\n');
            Paragraph(description, "    ");
        }

        var title = localizer["Commonage archive export"].Value;
        text.Append(title).Append('\n').Append(new string('=', title.Length)).Append("\n\n");
        Paragraph(localizer["This ZIP file holds a copy of a community archive made with Commonage. You need no special software to use it."]);
        Entry("archive.json", localizer["Everything that is known about the collections and items, complete and exact. Dates are ISO 8601 and in UTC. Each file lists its SHA-256 checksum, so you can check that a copy is intact."]);
        Entry("items.csv", localizer["The same items as a table, for spreadsheet programs. It is UTF-8. Cells that start with =, +, - or @ get a leading apostrophe, so a spreadsheet never runs them as formulas; archive.json has the exact text."]);
        text.Append("files/\n");
        Paragraph(localizer["The original files exactly as they were uploaded, in one folder per item:"], "    ");
        text.Length -= 1;
        text.Append("    files/<item id>/<file id>-<original file name>\n\n");
        Paragraph(localizer["Curators may have turned a photo: archive.json gives its \"rotation\" in degrees clockwise, and its \"position\" among the photos of the item."], "    ");
        Entry("branding/", localizer["The archive's logo, if it has one. Its name and pages are under \"settings\" in archive.json."]);
        Paragraph(localizer["\"includesPersonalData\" in archive.json says whether the names of contributors, photographers and authors and contributors' email addresses are included. If they are, keep this file as carefully as the archive itself."]);
        Paragraph(localizer["\"contentLanguage\" under \"settings\" says which language most of the material is written in, as a code such as \"de\" or \"pl\". \"mul\" means several languages, none of them most."]);
        Paragraph(localizer["A file with \"problem\": \"missing\" was not found on the server; \"checksumMismatch\" means it no longer matches the checksum taken when it was uploaded. Both should be looked into."]);
        Paragraph(localizer["The format is described in docs/operations.md of the Commonage source code."]);
        return text.ToString().TrimEnd() + "\n";
    }

    /// <summary>The README in English, whatever the language of whoever exports.</summary>
    private string EnglishReadme()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            return Readme(localizer);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}

internal record ExportSettings(
    string DefaultLanguage,
    string? ContentLanguage,
    string? OtherContentLanguages,
    string? AccentColor,
    string? TimeZone,
    string? DefaultRightsUri,
    bool AllowSearchEngines,
    bool RequireTwoFactor,
    bool LargePhotos,
    bool PrivacyReviewed,
    bool ImprintNotNeeded,
    DateTime? RestoreTestedAt,
    int? ActivityLogKeptYears,
    int? RejectedKeptMonths,
    bool AcceptVisitorNotes,
    string? Logo,
    IReadOnlyList<ExportText> Texts);

internal record ExportText(string Language, ArchiveTextKind Kind, string Text);

internal record ExportCollection(int Id, string Title, string? Description, DateTime CreatedAt, int Position, CollectionSort Sort, int? CoverFileId);

/// <summary>A tag and what it names. Items list their tags by name.</summary>
internal record ExportTag(string Name, TagKind Kind);

internal record ExportItem(
    int Id,
    int CollectionId,
    string Title,
    string? Description,
    ItemStatus Status,
    ExportDate? Date,
    IReadOnlyList<string> Tags,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? PublishedAt,
    DateTime? RejectedOrWithdrawnAt,
    string? RightsUri,
    string? RightsHolder,
    string? Credit,
    string? ReferenceNumber,
    string? OriginalLocation,
    string? ReceivedFrom,
    string? ReceivedOn,
    string? InternalNotes,
    bool? NeedsTranscription,
    bool HelpWanted,
    ExportContribution? Contribution,
    IReadOnlyList<ExportNote>? Notes,
    IReadOnlyList<ExportFile> Files);

/// <summary>A note from a visitor. Not in a copy of what is public; message, name and email address only with personal data.</summary>
internal record ExportNote(
    VisitorNoteKind Kind,
    string? Message,
    string? Name,
    string? Email,
    DateTime ReceivedAt,
    DateTime? HandledAt,
    DateTime? PersonalDetailsRemovedAt);

/// <summary>The date as written, and the earliest and latest day it can stand for.</summary>
internal record ExportDate(string Text, DateOnly? From, DateOnly? To);

internal record ExportContribution(
    string? ContributorName,
    string? ContributorEmail,
    bool ShowName,
    RightsStatement Rights,
    string? RightsHolder,
    string ConsentText,
    ReuseOffer? Reuse,
    bool ConsentCoversSharing,
    string? ReferenceCode,
    DateTime SubmittedAt,
    DateTime? ReviewedAt,
    DateTime? PersonalDetailsRemovedAt,
    string? ContributionLink);

internal record ExportFile(
    int Id,
    string? Path,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DateTime UploadedAt,
    string? AltText,
    string? Caption,
    int Position,
    int Rotation,
    string? Problem);

internal static class ItemsCsv
{
    private static readonly string[] Header =
    [
        "id", "collection", "title", "status", "date", "date_from", "date_to", "tags", "description",
        "rights", "rights_holder", "credit", "reference_number", "contributor_rights", "contributor_rights_holder",
        "contributor_name", "contributor_email", "files",
    ];

    public static async Task WriteAsync(
        Stream stream, IReadOnlyDictionary<int, string> collections, IAsyncEnumerable<ExportItem> items, CancellationToken cancellationToken)
    {
        // The byte order mark tells spreadsheet programs that the file is UTF-8.
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
        await WriteRowAsync(writer, Header);
        await foreach (var item in items.WithCancellation(cancellationToken))
        {
            await WriteRowAsync(writer,
            [
                item.Id.ToString(CultureInfo.InvariantCulture),
                collections.GetValueOrDefault(item.CollectionId),
                item.Title,
                item.Status.ToString(),
                item.Date?.Text,
                item.Date?.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                item.Date?.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                string.Join("; ", item.Tags),
                item.Description,
                item.RightsUri,
                item.RightsHolder,
                item.Credit,
                item.ReferenceNumber,
                item.Contribution?.Rights.ToString(),
                item.Contribution?.RightsHolder,
                item.Contribution?.ContributorName,
                item.Contribution?.ContributorEmail,
                string.Join("; ", item.Files.Select(f => f.Path ?? f.OriginalFileName)),
            ]);
        }
    }

    private static Task WriteRowAsync(StreamWriter writer, IEnumerable<string?> cells) =>
        writer.WriteAsync(string.Join(",", cells.Select(Cell)) + "\r\n");

    private static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        // Text sent in by anyone must not become a formula when the file is opened in a spreadsheet.
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
