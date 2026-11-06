using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Search;

/// <summary>Something an item still lacks, for curators working through what is left to do.</summary>
public enum ItemNeed
{
    Description,
    Date,
    Tags,
    Photos,
    PreviewFailed,
    Rights,
    AltText,
    Title,
    Transcription,
}

public enum AdminItemSort
{
    LastChanged,
    OldestFirst,
    Title,
}

public record AdminItemFilter(
    string? Text = null,
    ItemStatus? Status = null,
    int? CollectionId = null,
    string? Tag = null,
    ItemNeed? Needs = null,
    AdminItemSort? Sort = null)
{
    /// <summary>Waiting contributions are handled oldest first, so nobody waits longest.</summary>
    public AdminItemSort EffectiveSort => Sort ?? (Status == ItemStatus.Submitted ? AdminItemSort.OldestFirst : AdminItemSort.LastChanged);
}

/// <summary>One line of the curators' item list.</summary>
public record AdminItemRow(
    int Id, string Title, string CollectionTitle, ItemStatus Status, DateTime UpdatedAt, int? ThumbnailMediaFileId, int PhotoCount,
    string? FirstFileName = null, bool HasIdenticalFile = false);

/// <summary>The curators' item list. The edit page uses it too, to go on to the next item of the same list.</summary>
public static class AdminItemList
{
    public const string Page = "/Admin";

    public static async Task<IQueryable<ArchiveItem>> QueryAsync(this CommonageDbContext db, AdminItemFilter filter, CancellationToken cancellationToken)
    {
        // Curators see every item, drafts included.
        // A reference number is not in the search index, on purpose, so it is looked up on its own. Whoever types one wants that item.
        var isReference = filter.Text is { Length: > 0 } text && await db.ArchiveItems.AnyAsync(i => i.ReferenceNumber == text.Trim(), cancellationToken);
        var query = isReference ? null : await db.ToSearchQueryAsync(filter.Text, cancellationToken);
        var items = db.ArchiveItems.Filter(new ItemFilter(query, filter.Tag, filter.CollectionId));
        if (isReference)
            items = items.Where(i => i.ReferenceNumber == filter.Text!.Trim());
        if (filter.Status is { } status)
            items = items.Where(i => i.Status == status);

        items = filter.Needs switch
        {
            ItemNeed.Description => items.Where(i => i.Description == null),
            ItemNeed.Date => items.Where(i => i.DateText == null),
            ItemNeed.Tags => items.Where(i => i.TagNames == null),
            ItemNeed.Photos => items.Where(i => !i.MediaFiles.Any()),
            ItemNeed.PreviewFailed => items.Where(i => i.MediaFiles.Any(f => f.Status == ProcessingStatus.Failed)),
            ItemNeed.AltText => items.Where(i => i.MediaFiles.Any(f => f.AltText == null)),
            ItemNeed.Title => items.Where(i => i.Title == ""),
            ItemNeed.Transcription => items.Where(i => i.NeedsTranscription),
            // With a default for the whole archive, nothing is left undecided.
            ItemNeed.Rights when !await db.ArchiveSettings.AnyAsync(s => s.DefaultRightsUri != null, cancellationToken) => items.Where(i => i.RightsUri == null),
            ItemNeed.Rights => items.Where(i => false),
            _ => items,
        };

        return filter.EffectiveSort switch
        {
            AdminItemSort.OldestFirst => items.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id),
            AdminItemSort.Title => items.OrderBy(i => i.Title).ThenBy(i => i.Id),
            _ => items.OrderByDescending(i => i.UpdatedAt).ThenByDescending(i => i.Id),
        };
    }

    /// <summary>The filter behind a link to the list, or null if the address is not that list.</summary>
    public static AdminItemFilter? FilterFrom(string? listUrl)
    {
        if (listUrl is null || !listUrl.Equals(Page, StringComparison.OrdinalIgnoreCase) && !listUrl.StartsWith(Page + "?", StringComparison.OrdinalIgnoreCase))
            return null;

        var at = listUrl.IndexOf('?');
        var query = at < 0 ? [] : QueryHelpers.ParseQuery(listUrl[at..]);
        string? Value(string key) => query.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value.ToString() : null;
        return new AdminItemFilter(
            Value("q"),
            Enum.TryParse<ItemStatus>(Value("status"), out var status) ? status : null,
            int.TryParse(Value("collection"), out var collection) ? collection : null,
            Value("tag"),
            Enum.TryParse<ItemNeed>(Value("needs"), out var needs) ? needs : null,
            Enum.TryParse<AdminItemSort>(Value("sort"), out var sort) ? sort : null);
    }

    /// <summary>The items before and after this one in the list.</summary>
    public static async Task<(int? Previous, int? Next)> NeighboursAsync(this CommonageDbContext db, AdminItemFilter filter, int id, CancellationToken cancellationToken)
    {
        var ids = await (await db.QueryAsync(filter, cancellationToken)).Select(i => i.Id).ToListAsync(cancellationToken);
        var at = ids.IndexOf(id);
        if (at < 0)
            return (null, null);
        return (at > 0 ? ids[at - 1] : null, at < ids.Count - 1 ? ids[at + 1] : null);
    }
}
