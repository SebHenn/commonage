using System.Linq.Expressions;
using Commonage.Web.Domain;

namespace Commonage.Web.Search;

/// <summary>What an item card in a list or search result shows.</summary>
public record ItemSummary(int Id, string Title, string? DateText, int? ThumbnailMediaFileId)
{
    public static readonly Expression<Func<ArchiveItem, ItemSummary>> FromItem = i => new ItemSummary(
        i.Id,
        i.Title,
        i.DateText,
        i.MediaFiles.Where(f => f.Status == ProcessingStatus.Ready).OrderBy(f => f.Id).Select(f => (int?)f.Id).FirstOrDefault());
}
