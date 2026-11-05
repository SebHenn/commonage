using Commonage.Web.Data;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using Commonage.Web.Domain;

namespace Commonage.Web.Search;

/// <summary>What an item card in a list or search result shows.</summary>
public record ItemSummary(int Id, string Title, string? DateText, int? ThumbnailMediaFileId, string CollectionTitle)
{
    public static readonly Expression<Func<ArchiveItem, ItemSummary>> FromItem = i => new ItemSummary(
        i.Id,
        i.Title,
        i.DateText,
        i.MediaFiles.Where(f => f.Status == ProcessingStatus.Ready).OrderBy(f => f.Id).Select(f => (int?)f.Id).FirstOrDefault(),
        i.Collection.Title);
}

/// <summary>A collection as visitors find it. One without anything public yet has nothing to show them.</summary>
public record CollectionCard(int Id, string Title, string? Description, int PublishedItemCount, int? CoverMediaFileId);

public static class CollectionCards
{
    public static Task<List<CollectionCard>> LoadCollectionCardsAsync(this CommonageDbContext db, CancellationToken cancellationToken) =>
        db.Collections
            .Where(c => c.Items.Any(i => i.Status == ItemStatus.Published))
            .OrderBy(c => c.Title)
            .Select(c => new CollectionCard(
                c.Id,
                c.Title,
                c.Description,
                c.Items.Count(i => i.Status == ItemStatus.Published),
                c.Items.Where(i => i.Status == ItemStatus.Published)
                    .SelectMany(i => i.MediaFiles)
                    .Where(f => f.Status == ProcessingStatus.Ready)
                    .OrderBy(f => f.Id)
                    .Select(f => (int?)f.Id)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);
}
