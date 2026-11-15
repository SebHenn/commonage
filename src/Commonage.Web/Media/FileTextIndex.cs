using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Commonage.Web.Media;

/// <summary>
/// Keeps <see cref="ItemFileText"/> in step with the PDFs an item has. Called wherever a PDF becomes ready, goes to the bin or
/// comes back, because a file in the bin must not be found by its text. It only changes the context; the caller saves.
/// </summary>
public static class FileTextIndex
{
    /// <summary>Shortens the text without leaving half of a character pair at the end, which the database refuses.</summary>
    public static string Cut(string text, int length)
    {
        if (text.Length > length)
            text = text[..length];
        return text.Length > 0 && char.IsHighSurrogate(text[^1]) ? text[..^1] : text;
    }

    public static async Task RebuildAsync(CommonageDbContext db, int itemId, CancellationToken cancellationToken)
    {
        // The query filters leave out files in the bin and files of an item in the bin.
        var texts = await db.MediaFiles
            .Where(f => f.ArchiveItemId == itemId && f.Status == ProcessingStatus.Ready && f.ExtractedText != null)
            .OrderBy(f => f.Position).ThenBy(f => f.Id)
            .Select(f => f.ExtractedText!)
            .ToListAsync(cancellationToken);
        var joined = string.Join("\n\n", texts);
        joined = Cut(joined, ItemFileText.MaxLength);

        var row = await db.ItemFileTexts.FirstOrDefaultAsync(t => t.ArchiveItemId == itemId, cancellationToken);
        if (joined.Length == 0)
        {
            if (row is not null)
                db.ItemFileTexts.Remove(row);
        }
        else if (row is null)
        {
            db.ItemFileTexts.Add(new ItemFileText { ArchiveItemId = itemId, Text = joined });
        }
        else
        {
            row.Text = joined;
        }
    }
}
