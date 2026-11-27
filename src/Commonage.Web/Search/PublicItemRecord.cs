using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Operations;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Search;

/// <summary>
/// What a public item says about itself to a program: the JSON at <c>/items/{id}.json</c> and the Dublin Core tags in the page.
/// Written out field by field (an allow-list), never made from an entity, so a field added to <see cref="ArchiveItem"/> later
/// stays private until someone adds it here on purpose.
/// </summary>
/// <param name="Identifier">The address of the item's page.</param>
/// <param name="DateText">The date as written ("circa 1950"); <paramref name="DateFrom"/> and <paramref name="DateTo"/> are the days it can stand for.</param>
/// <param name="Credit">Exactly what the page prints as the credit.</param>
/// <param name="Rights">The address of what others may do with it.</param>
/// <param name="Type">A DCMI type (Image, Sound, Text), only from files visitors get.</param>
/// <param name="DeceasedPeople">The main names of the item's public people who have died, and nobody who may be living.</param>
/// <param name="Description">Left out, with the files, when the contributor did not agree to sharing beyond the website.</param>
public sealed record PublicItemRecord(
    string Identifier,
    string Title,
    string? Description,
    string? DateText,
    string? DateFrom,
    string? DateTo,
    string? Credit,
    string? Rights,
    string? Language,
    IReadOnlyList<string> Subjects,
    [property: JsonPropertyName("people")] IReadOnlyList<string> DeceasedPeople,
    string? Type,
    string Collection,
    IReadOnlyList<PublicFileRecord> Files)
{
    /// <summary>The page of a visible item. Needs the collection, contribution and tags loaded; the files are the ones visitors may have.</summary>
    public static PublicItemRecord From(ArchiveItem item, IReadOnlyCollection<MediaFile> visibleFiles, ArchiveIdentity identity, string baseAddress, IReadOnlyList<string> deceasedPeople)
    {
        var travels = item.MayTravel;
        var files = travels && item.MediaLevel == MediaLevel.Everything
            ? visibleFiles.Where(f => f.Status == ProcessingStatus.Ready).OrderBy(f => f.Position).ThenBy(f => f.Id)
                .Select(f => PublicFileRecord.From(f, baseAddress)).ToList()
            : [];
        return new PublicItemRecord(
            $"{baseAddress}/items/{item.Id}",
            item.Title,
            travels && !string.IsNullOrWhiteSpace(item.Description) ? item.Description : null,
            item.DateText,
            Day(item.DateFrom),
            Day(item.DateTo),
            item.Credit,
            item.RightsUri ?? identity.DefaultRightsUri,
            identity.MaterialLanguage,
            item.Tags.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => t.Name).ToList(),
            deceasedPeople,
            files.Count == 0 ? null : files[0].Kind switch { "audio" => "Sound", "document" => "Text", _ => "Image" },
            item.Collection.Title,
            files);
    }

    public string JsonAddress => $"{Identifier}.json";

    private static string? Day(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <param name="Kind">image, audio or document.</param>
/// <param name="Url">A picture's page-sized copy, a recording's tag-free copy, or a document's first page image.</param>
public sealed record PublicFileRecord(string Kind, string Url, string? AltText, string? Caption, int? Pages)
{
    public static PublicFileRecord From(MediaFile file, string baseAddress) =>
        file.IsAudio ? new("audio", $"{baseAddress}/media/{file.Id}/audio", null, file.Caption, null)
        : file.IsPdf && file.PageCount is > 0 ? new("document", $"{baseAddress}/media/{file.Id}/page/1", null, file.Caption, file.PageCount)
        : new("image", $"{baseAddress}/media/{file.Id}/preview", file.AltText, file.Caption, null);
}

/// <summary>
/// <c>/items/{id}.json</c>. An item a visitor cannot see answers exactly as its page does (the same empty "not found", which the
/// error page fills in), so the address cannot be used to find out that something exists. The answer is cached for ten minutes
/// like the feed, and carries no <c>ETag</c> or <c>Last-Modified</c>, because the time of the last edit could be one made during an embargo.
/// </summary>
public static class PublicItemJson
{
    private const string CacheControl = "public, max-age=600";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Any script stays readable. Markup characters are still escaped.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = true,
    };

    public static void MapPublicItemJson(this WebApplication app) => app.MapGet("/items/{id:int}.json", ServeAsync);

    private static async Task<IResult> ServeAsync(
        int id, HttpContext context, CommonageDbContext db, ArchiveProfile profile, CancellationToken cancellationToken)
    {
        var item = await db.VisibleItems
            .Include(i => i.Collection)
            .Include(i => i.Contribution)
            .Include(i => i.Tags)
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
            return Results.NotFound();

        var files = await db.VisibleMedia
            .Where(f => f.ArchiveItemId == id && f.Status == ProcessingStatus.Ready)
            .OrderBy(f => f.Position).ThenBy(f => f.Id)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var identity = await profile.GetAsync();
        var record = PublicItemRecord.From(item, files, identity, context.Request.BaseAddress(), await VisiblePeople.DeceasedOfItemAsync(db, id, cancellationToken));

        context.Response.Headers.CacheControl = CacheControl;
        if (!identity.AllowSearchEngines)
            context.Response.Headers["X-Robots-Tag"] = "noindex";
        return Results.Json(record, Options, "application/json");
    }
}
