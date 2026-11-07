using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Commonage.Web.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Search;

/// <summary>What a link shows when it is pasted somewhere, such as a messenger, and what a page is really called.</summary>
public record LinkPreview(string Title, string? Description, string Url, string? ImageUrl = null, string? ImageAlt = null, string Type = "website")
{
    private const int MaxDescriptionLength = 200;

    /// <summary>The beginning of a text, cut between words. A letter's first lines say more than none.</summary>
    public static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var oneLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (oneLine.Length <= MaxDescriptionLength)
            return oneLine;
        var end = oneLine.LastIndexOf(' ', MaxDescriptionLength);
        return $"{oneLine[..(end > 0 ? end : MaxDescriptionLength)]}…";
    }
}

/// <summary>
/// A feed of what is new and a list of all public pages. Both exist for programs that find an archive, so both follow the switch
/// for search engines. Neither says anything a public page does not say.
/// </summary>
public static class PublicFeeds
{
    public const string FeedPath = "/feed.xml";
    public const string SitemapPath = "/sitemap.xml";

    private const int FeedLength = 30;

    // A sitemap may hold at most 50,000 addresses.
    private const int MaxSitemapItems = 49_000;
    private const string CacheControl = "public, max-age=600";

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";

    public static void MapPublicFeeds(this WebApplication app)
    {
        app.MapGet(FeedPath, ServeFeedAsync);
        app.MapGet(SitemapPath, ServeSitemapAsync);
    }

    public static string BaseAddress(this HttpRequest request) => $"{request.Scheme}://{request.Host}";

    private static async Task<IResult> ServeFeedAsync(
        HttpContext context, CommonageDbContext db, ArchiveProfile profile, IStringLocalizer<SharedResource> localizer, CancellationToken cancellationToken)
    {
        var identity = await profile.GetAsync();
        if (!identity.AllowSearchEngines)
            return Results.NotFound();

        var address = context.Request.BaseAddress();
        // What people agreed to before this existed was "this website". A feed is copied to other places, so those stay out of it.
        var items = await db.PublishedItems
            .Where(i => i.Contribution == null || i.Contribution.ConsentCoversSharing)
            .Include(i => i.MediaFiles.Where(f => f.Status == ProcessingStatus.Ready).OrderBy(f => f.Position).ThenBy(f => f.Id).Take(1))
            .OrderByDescending(i => i.PublishedAt).ThenByDescending(i => i.Id)
            .Take(FeedLength)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var updated = items.Count == 0 ? DateTime.UtcNow : items.Max(i => i.UpdatedAt);
        var feed = new XElement(Atom + "feed",
            new XAttribute(XNamespace.Xml + "lang", CultureInfo.CurrentUICulture.Name),
            new XElement(Atom + "id", $"{address}{FeedPath}"),
            new XElement(Atom + "title", XmlText(localizer["What is new in {0}", identity.Name])),
            new XElement(Atom + "updated", Timestamp(updated)),
            new XElement(Atom + "link", new XAttribute("rel", "self"), new XAttribute("type", "application/atom+xml"), new XAttribute("href", $"{address}{FeedPath}")),
            new XElement(Atom + "link", new XAttribute("rel", "alternate"), new XAttribute("type", "text/html"), new XAttribute("href", $"{address}/")),
            new XElement(Atom + "author", new XElement(Atom + "name", XmlText(identity.Name))),
            items.Select(item => Entry(item, address, identity, localizer)));

        context.Response.Headers.CacheControl = CacheControl;
        return Results.Text(Written(feed), "application/atom+xml", Encoding.UTF8);
    }

    private static XElement Entry(ArchiveItem item, string address, ArchiveIdentity identity, IStringLocalizer localizer)
    {
        var url = $"{address}/items/{item.Id}";
        var rights = RightsOptions.LabelOrAsk(localizer, item.RightsUri ?? identity.DefaultRightsUri);
        var entry = new XElement(Atom + "entry",
            new XElement(Atom + "id", url),
            new XElement(Atom + "title", XmlText(item.Title)),
            new XElement(Atom + "link", new XAttribute("rel", "alternate"), new XAttribute("type", "text/html"), new XAttribute("href", url)),
            new XElement(Atom + "published", Timestamp(item.PublishedAt ?? item.UpdatedAt)),
            new XElement(Atom + "updated", Timestamp(item.UpdatedAt)),
            new XElement(Atom + "rights", XmlText(rights)));

        var content = new StringBuilder();
        if (item.MediaFiles.FirstOrDefault() is { } photo)
        {
            var alt = photo.AltText ?? item.Title;
            content.Append(CultureInfo.InvariantCulture, $"""<p><img src="{address}/media/{photo.Id}/card" alt="{WebUtility.HtmlEncode(alt)}"></p>""");
        }
        if (!string.IsNullOrWhiteSpace(item.Description))
            content.Append(CultureInfo.InvariantCulture, $"<p>{WebUtility.HtmlEncode(item.Description)}</p>");
        if (item.Credit is { } credit)
            content.Append(CultureInfo.InvariantCulture, $"<p>{WebUtility.HtmlEncode(credit)}</p>");
        if (content.Length > 0)
            entry.Add(new XElement(Atom + "content", new XAttribute("type", "html"), XmlText(content.ToString())));
        return entry;
    }

    private static async Task<IResult> ServeSitemapAsync(HttpContext context, CommonageDbContext db, ArchiveProfile profile, CancellationToken cancellationToken)
    {
        if (!(await profile.GetAsync()).AllowSearchEngines)
            return Results.NotFound();

        var address = context.Request.BaseAddress();
        var collections = await db.Collections
            .Where(c => c.Items.Any(i => i.Status == ItemStatus.Published))
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        var items = await db.PublishedItems
            .OrderByDescending(i => i.PublishedAt).ThenByDescending(i => i.Id)
            .Take(MaxSitemapItems)
            .Select(i => new { i.Id, i.UpdatedAt })
            .ToListAsync(cancellationToken);

        static XElement Page(string url, DateTime? changed = null) => new(Sitemap + "url",
            new XElement(Sitemap + "loc", url),
            changed is { } at ? new XElement(Sitemap + "lastmod", at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : null);

        var sitemap = new XElement(Sitemap + "urlset",
            Page($"{address}/"),
            Page($"{address}/collections"),
            collections.Select(id => Page($"{address}/collections/{id}")),
            items.Select(item => Page($"{address}/items/{item.Id}", item.UpdatedAt)));

        context.Response.Headers.CacheControl = CacheControl;
        return Results.Text(Written(sitemap), "application/xml", Encoding.UTF8);
    }

    private static string Written(XElement root) => new XDeclaration("1.0", "utf-8", null) + "\n" + root.ToString(SaveOptions.DisableFormatting);

    private static string Timestamp(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>A pasted text can hold control characters. A web page shrugs those off, but one of them makes XML unreadable as a whole.</summary>
    private static string XmlText(string text) => new(text.Where(XmlConvert.IsXmlChar).ToArray());
}
