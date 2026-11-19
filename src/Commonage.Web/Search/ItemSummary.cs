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
        // A record without its media has no picture, also not as a thumbnail.
        i.MediaLevel != MediaLevel.Everything ? null : i.MediaFiles.Where(f => f.Status == ProcessingStatus.Ready && !f.ContentType.StartsWith("audio/")).OrderBy(f => f.Position).ThenBy(f => f.Id).Select(f => (int?)f.Id).FirstOrDefault(),
        i.Collection.Title);
}

/// <summary>A collection as visitors find it. One without anything public yet has nothing to show them.</summary>
public record CollectionCard(int Id, string Title, string? Description, int PublishedItemCount, int? CoverMediaFileId, YearSpan Years);

public static class CollectionCards
{
    /// <summary>Collections with published items, in the order curators gave them.</summary>
    public static Task<List<CollectionCard>> LoadCollectionCardsAsync(this CommonageDbContext db, CancellationToken cancellationToken) =>
        db.Collections
            .Where(c => c.Items.AsQueryable().Any(Visibility.Item(db.Today)))
            .OrderBy(c => c.Position).ThenBy(c => c.Title).ThenBy(c => c.Id)
            .ToCards(db.Today)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// What visitors see of each collection, from its published items only: drafts, items in the bin and their photos count
    /// for nothing, and a chosen cover whose item is not published, or has moved to another collection, is passed over.
    /// </summary>
    public static IQueryable<CollectionCard> ToCards(this IQueryable<Collection> collections, DateOnly today) =>
        collections.Select(c => new CollectionCard(
            c.Id,
            c.Title,
            c.Description,
            c.Items.AsQueryable().Count(Visibility.Item(today)),
            c.Items.AsQueryable().Where(Visibility.ItemWithMedia(today))
                .SelectMany(i => i.MediaFiles)
                .Where(f => f.Status == ProcessingStatus.Ready && f.Id == c.CoverMediaFileId && !f.ContentType.StartsWith("audio/"))
                .Select(f => (int?)f.Id)
                .FirstOrDefault()
            ?? c.Items.AsQueryable().Where(Visibility.ItemWithMedia(today))
                .SelectMany(i => i.MediaFiles)
                .Where(f => f.Status == ProcessingStatus.Ready && !f.ContentType.StartsWith("audio/"))
                .OrderBy(f => f.ArchiveItemId).ThenBy(f => f.Position).ThenBy(f => f.Id)
                .Select(f => (int?)f.Id)
                .FirstOrDefault(),
            new YearSpan(
                c.Items.AsQueryable().Where(Visibility.Item(today)).Min(i => (int?)(i.DateFrom ?? i.DateTo)!.Value.Year),
                c.Items.AsQueryable().Where(Visibility.Item(today)).Max(i => (int?)(i.DateTo ?? i.DateFrom)!.Value.Year))));
}
