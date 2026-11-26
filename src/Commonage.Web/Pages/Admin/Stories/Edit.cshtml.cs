using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Commonage.Web.Audit;
using Commonage.Web.Data;
using Commonage.Web.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Commonage.Web.Pages.Admin.Stories;

/// <summary>
/// A story's title and summary, and its blocks: text and items, in the order a curator gives them. Changing the blocks
/// never touches the story's own row, so a title form that is open stays savable and nothing about the story's
/// version or date moves.
/// </summary>
public partial class EditModel(CommonageDbContext db, AuditLog audit, IStringLocalizer<SharedResource> localizer, TimeProvider clock) : PageModel
{
    public Story Story { get; private set; } = null!;

    public InputModel Input { get; private set; } = new();

    /// <summary>The story as another curator saved it, when the title form was sent from an older version.</summary>
    public Story? SavedByOthers { get; private set; }

    public IReadOnlyList<BlockRow> Blocks { get; private set; } = [];

    /// <summary>The block that was just added or moved. The page puts the keyboard focus on it.</summary>
    public int? FocusBlockId { get; private set; }

    /// <summary>Text typed into a block's form that did not go through, so that it is not lost.</summary>
    public Dictionary<int, string> TypedText { get; } = [];

    public string? TypedNewText { get; private set; }
    public string? TypedItemReference { get; private set; }
    public string? TypedItemCaption { get; private set; }

    /// <summary>Whether visitors would see an item block, and if not, why. Curators see this under /Admin only.</summary>
    public enum ItemState
    {
        Shown,
        ShownWithoutPhotos,
        Draft,
        PublicFrom,
        InBin,
        Withdrawn,
        Missing,
    }

    public record BlockRow(int Id, StoryBlockKind Kind, string? Text, string? Caption, int? ItemId, string? ItemTitle, string? DateText,
        int? ThumbnailMediaFileId, ItemState State, DateOnly? PublicFrom);

    public class InputModel
    {
        [Required(ErrorMessage = "Please enter a title.")]
        [StringLength(Story.TitleMaxLength, ErrorMessage = "The title can be at most {1} characters long.")]
        [Display(Name = "Title")]
        public string Title { get; set; } = "";

        [StringLength(Story.SummaryMaxLength, ErrorMessage = "The summary can be at most {1} characters long.")]
        [Display(Name = "Summary")]
        public string? Summary { get; set; }

        public uint? Version { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(int id, int? block, CancellationToken cancellationToken)
    {
        var story = await db.Stories.FindAsync([id], cancellationToken);
        if (story is null)
            return NotFound();

        Input = new InputModel { Title = story.Title, Summary = story.Summary, Version = story.Version };
        FocusBlockId = block;
        await LoadAsync(story, cancellationToken);
        return Page();
    }

    /// <summary>Saves the title and summary. If someone else saved them since the form was opened, nothing is overwritten.</summary>
    public async Task<IActionResult> OnPostAsync(int id, [Bind(Prefix = "Input")] InputModel input, CancellationToken cancellationToken)
    {
        var story = await db.Stories.FindAsync([id], cancellationToken);
        if (story is null)
            return NotFound();

        Input = input;
        if (!ModelState.IsValid)
        {
            await LoadAsync(story, cancellationToken);
            return Page();
        }

        if (input.Version is { } version)
            db.Entry(story).Property(s => s.Version).OriginalValue = version;

        var title = input.Title.Trim();
        var summary = string.IsNullOrWhiteSpace(input.Summary) ? null : input.Summary.Trim();
        if (title != story.Title || summary != story.Summary)
        {
            story.Title = title;
            story.Summary = summary;
            story.Touch(clock.GetUtcNow().UtcDateTime);
            audit.Record(AuditAction.StoryChanged, story.Title, story: story);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var current = await db.Stories.FindAsync([id], cancellationToken);
            if (current is null)
                return NotFound();

            SavedByOthers = new Story { Title = current.Title, Summary = current.Summary };
            // The next save is a deliberate decision to replace the other version, so it may go through.
            ModelState.Remove("Input.Version");
            Input = new InputModel { Title = input.Title, Summary = input.Summary, Version = current.Version };
            await LoadAsync(current, cancellationToken);
            return Page();
        }

        TempData["Message"] = localizer["Your changes are saved."].Value;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostPublishAsync(int id, CancellationToken cancellationToken)
    {
        var story = await db.Stories.FindAsync([id], cancellationToken);
        if (story is null)
            return NotFound();
        if (!story.CanBePublished)
            return BadRequest();

        if (story.Status != StoryStatus.Published)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            story.Publish(now);
            story.Touch(now);
            audit.Record(AuditAction.StoryPublished, story.Title, story: story);
            await db.SaveChangesAsync(cancellationToken);
        }

        TempData["Message"] = localizer["The story is published."].Value;
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUnpublishAsync(int id, CancellationToken cancellationToken)
    {
        var story = await db.Stories.FindAsync([id], cancellationToken);
        if (story is null)
            return NotFound();

        if (story.Status == StoryStatus.Published)
        {
            story.Unpublish();
            story.Touch(clock.GetUtcNow().UtcDateTime);
            audit.Record(AuditAction.StoryUnpublished, story.Title, story: story);
            await db.SaveChangesAsync(cancellationToken);
        }

        TempData["Message"] = localizer["The story is a draft again."].Value;
        return RedirectToPage(new { id });
    }

    /// <summary>Moves the story to the bin: visitors lose it at once, and it can be restored there for 30 days. Its parts stay with it.</summary>
    public async Task<IActionResult> OnPostMoveToBinAsync(int id, CancellationToken cancellationToken)
    {
        var story = await db.Stories.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (story is null)
            return NotFound();

        story.MoveToBin(clock.GetUtcNow().UtcDateTime);
        audit.Record(AuditAction.StoryMovedToBin, story.Title, story: story);
        await db.SaveChangesAsync(cancellationToken);

        TempData["Message"] = localizer.Plural("The story is in the bin. You can restore it there for {0, plural, one {# day} other {# days}}.", ArchiveBin.RetentionDays);
        return RedirectToPage("/Admin/Stories/Index");
    }

    public async Task<IActionResult> OnPostAddTextAsync(int id, string? text, CancellationToken cancellationToken)
    {
        var story = await db.Stories.FindAsync([id], cancellationToken);
        if (story is null)
            return NotFound();

        var words = Clean(text);
        if (words is null)
            ModelState.AddModelError("newText", localizer["Please write some text."]);
        else if (words.Length > StoryBlock.TextMaxLength)
            ModelState.AddModelError("newText", localizer["The text can be at most {0} characters long.", StoryBlock.TextMaxLength]);
        else if (await IsFullAsync(id, cancellationToken))
            ModelState.AddModelError("newText", FullMessage());
        if (!ModelState.IsValid)
        {
            TypedNewText = text;
            return await ShowAsync(story, cancellationToken);
        }

        var block = new StoryBlock { StoryId = id, Kind = StoryBlockKind.Text, Text = words, Position = await db.StoryBlocks.CountAsync(b => b.StoryId == id, cancellationToken) };
        db.StoryBlocks.Add(block);
        audit.Record(AuditAction.StoryChanged, story.Title, story: story);
        await db.SaveChangesAsync(cancellationToken);
        return BackToBlock(id, block.Id);
    }

    /// <summary>Adds an item by its number or by the address of its page, on the website or under /Admin.</summary>
    public async Task<IActionResult> OnPostAddItemAsync(int id, string? item, string? caption, CancellationToken cancellationToken)
    {
        var story = await db.Stories.FindAsync([id], cancellationToken);
        if (story is null)
            return NotFound();

        var number = ItemNumber(item);
        // A curator may add an item visitors do not see yet; items in the bin or turned down are not offered.
        var found = number is { } n
            ? await db.ArchiveItems.IgnoreQueryFilters().Where(i => i.Id == n)
                .Select(i => new { i.Id, i.Status, i.DeletedAt }).FirstOrDefaultAsync(cancellationToken)
            : null;
        var cleanCaption = Clean(caption);
        if (number is null)
            ModelState.AddModelError("item", localizer["Please enter the number of an item, or the address of its page."]);
        else if (found is null)
            ModelState.AddModelError("item", localizer["There is no item with this number."]);
        else if (found.DeletedAt is not null)
            ModelState.AddModelError("item", localizer["This item is in the bin. Take it out of the bin first."]);
        else if (found.Status is ItemStatus.Rejected or ItemStatus.Withdrawn)
            ModelState.AddModelError("item", localizer["This item was turned down or withdrawn, and cannot be part of a story."]);
        else if (cleanCaption is { Length: > StoryBlock.CaptionMaxLength })
            ModelState.AddModelError("caption", localizer["The caption can be at most {0} characters long.", StoryBlock.CaptionMaxLength]);
        else if (await IsFullAsync(id, cancellationToken))
            ModelState.AddModelError("item", FullMessage());
        if (!ModelState.IsValid || found is null)
        {
            TypedItemReference = item;
            TypedItemCaption = caption;
            return await ShowAsync(story, cancellationToken);
        }

        var block = new StoryBlock
        {
            StoryId = id,
            Kind = StoryBlockKind.Item,
            ArchiveItemId = found.Id,
            Caption = cleanCaption,
            Position = await db.StoryBlocks.CountAsync(b => b.StoryId == id, cancellationToken),
        };
        db.StoryBlocks.Add(block);
        audit.Record(AuditAction.StoryItemAdded, story.Title, await db.ArchiveItems.FindAsync([found.Id], cancellationToken), story);
        await db.SaveChangesAsync(cancellationToken);

        if (!await db.VisibleItems.AnyAsync(i => i.Id == found.Id, cancellationToken))
            TempData["Message"] = localizer["The item is added. Visitors will not see it until it is public."].Value;
        return BackToBlock(id, block.Id);
    }

    public async Task<IActionResult> OnPostSaveTextAsync(int id, int blockId, string? text, CancellationToken cancellationToken)
    {
        var block = await FindBlockAsync(id, blockId, StoryBlockKind.Text, cancellationToken);
        if (block is null)
            return NotFound();

        var words = Clean(text);
        if (words is null)
            ModelState.AddModelError($"text-{blockId}", localizer["Please write some text."]);
        else if (words.Length > StoryBlock.TextMaxLength)
            ModelState.AddModelError($"text-{blockId}", localizer["The text can be at most {0} characters long.", StoryBlock.TextMaxLength]);
        if (!ModelState.IsValid)
        {
            TypedText[blockId] = text ?? "";
            return await ShowAsync(await db.Stories.FindAsync([id], cancellationToken), cancellationToken);
        }

        if (words != block.Text)
        {
            block.Text = words;
            var story = (await db.Stories.FindAsync([id], cancellationToken))!;
            audit.Record(AuditAction.StoryChanged, story.Title, story: story);
            await db.SaveChangesAsync(cancellationToken);
        }

        return BackToBlock(id, blockId);
    }

    public async Task<IActionResult> OnPostSaveCaptionAsync(int id, int blockId, string? caption, CancellationToken cancellationToken)
    {
        var block = await FindBlockAsync(id, blockId, StoryBlockKind.Item, cancellationToken);
        if (block is null)
            return NotFound();

        var words = Clean(caption);
        if (words is { Length: > StoryBlock.CaptionMaxLength })
        {
            ModelState.AddModelError($"caption-{blockId}", localizer["The caption can be at most {0} characters long.", StoryBlock.CaptionMaxLength]);
            TypedText[blockId] = caption ?? "";
            return await ShowAsync(await db.Stories.FindAsync([id], cancellationToken), cancellationToken);
        }

        if (words != block.Caption)
        {
            block.Caption = words;
            var story = (await db.Stories.FindAsync([id], cancellationToken))!;
            audit.Record(AuditAction.StoryChanged, story.Title, story: story);
            await db.SaveChangesAsync(cancellationToken);
        }

        return BackToBlock(id, blockId);
    }

    /// <summary>Puts a block earlier or later. Only block rows change: the story's own row, version and date stay as they are.</summary>
    /// <param name="direction">"earlier" or "later".</param>
    public async Task<IActionResult> OnPostMoveBlockAsync(int id, int blockId, string? direction, CancellationToken cancellationToken)
    {
        var story = await db.Stories.FindAsync([id], cancellationToken);
        if (story is null)
            return NotFound();

        var blocks = await db.StoryBlocks.Where(b => b.StoryId == id).OrderBy(b => b.Position).ThenBy(b => b.Id).ToListAsync(cancellationToken);
        var from = blocks.FindIndex(b => b.Id == blockId);
        if (from < 0)
            return NotFound();

        var to = direction switch
        {
            "earlier" => Math.Max(from - 1, 0),
            "later" => Math.Min(from + 1, blocks.Count - 1),
            _ => from,
        };
        if (to == from)
            return BackToBlock(id, blockId);
        var block = blocks[from];
        blocks.RemoveAt(from);
        blocks.Insert(to, block);
        for (var position = 0; position < blocks.Count; position++)
            blocks[position].Position = position;

        await audit.RecordStoryBlocksArrangedAsync(story, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return BackToBlock(id, blockId);
    }

    public async Task<IActionResult> OnPostRemoveBlockAsync(int id, int blockId, CancellationToken cancellationToken)
    {
        var story = await db.Stories.FindAsync([id], cancellationToken);
        if (story is null)
            return NotFound();

        var blocks = await db.StoryBlocks.Where(b => b.StoryId == id).OrderBy(b => b.Position).ThenBy(b => b.Id).ToListAsync(cancellationToken);
        var block = blocks.Find(b => b.Id == blockId);
        if (block is null)
            return NotFound();

        var item = block.ArchiveItemId is { } itemId ? await db.ArchiveItems.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken) : null;
        blocks.Remove(block);
        db.StoryBlocks.Remove(block);
        for (var position = 0; position < blocks.Count; position++)
            blocks[position].Position = position;

        audit.Record(block.Kind == StoryBlockKind.Item ? AuditAction.StoryItemRemoved : AuditAction.StoryChanged, story.Title, item, story);
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToPage(new { id });
    }

    private async Task<StoryBlock?> FindBlockAsync(int storyId, int blockId, StoryBlockKind kind, CancellationToken cancellationToken) =>
        await db.StoryBlocks.FirstOrDefaultAsync(b => b.Id == blockId && b.StoryId == storyId && b.Kind == kind, cancellationToken);

    private async Task<bool> IsFullAsync(int id, CancellationToken cancellationToken) =>
        await db.StoryBlocks.CountAsync(b => b.StoryId == id, cancellationToken) >= Story.MaxBlocks;

    private string FullMessage() => localizer.Plural("A story can have at most {0, plural, one {# part} other {# parts}}.", Story.MaxBlocks);

    /// <summary>Back to the page at the block, with the keyboard focus on it and also without JavaScript.</summary>
    private IActionResult BackToBlock(int id, int blockId) =>
        RedirectToPage("Edit", null, new { id, block = blockId }, $"block-{blockId}");

    private async Task<IActionResult> ShowAsync(Story? story, CancellationToken cancellationToken)
    {
        if (story is null)
            return NotFound();

        Input = new InputModel { Title = story.Title, Summary = story.Summary, Version = story.Version };
        await LoadAsync(story, cancellationToken);
        return Page();
    }

    private async Task LoadAsync(Story story, CancellationToken cancellationToken)
    {
        Story = story;
        var blocks = await db.StoryBlocks.AsNoTracking().Where(b => b.StoryId == story.Id)
            .OrderBy(b => b.Position).ThenBy(b => b.Id).ToListAsync(cancellationToken);
        var ids = blocks.Where(b => b.ArchiveItemId is not null).Select(b => b.ArchiveItemId!.Value).Distinct().ToList();

        // Curators see every item and why visitors may not, so the items come with the bin and the drafts.
        var items = ids.Count == 0 ? [] : await db.ArchiveItems.IgnoreQueryFilters().Where(i => ids.Contains(i.Id))
            .Select(i => new
            {
                i.Id,
                i.Title,
                i.DateText,
                i.Status,
                i.PublicFrom,
                i.DeletedAt,
                Thumbnail = i.MediaFiles.Where(f => f.DeletedAt == null && f.Status == ProcessingStatus.Ready && !f.ContentType.StartsWith("audio/"))
                    .OrderBy(f => f.Position).ThenBy(f => f.Id).Select(f => (int?)f.Id).FirstOrDefault(),
            }).ToDictionaryAsync(i => i.Id, cancellationToken);
        // The one definition of "visitors see it", with and without its photos.
        var shown = ids.Count == 0 ? [] : (await db.VisibleItems.Where(i => ids.Contains(i.Id)).Select(i => i.Id).ToListAsync(cancellationToken)).ToHashSet();
        var shownWithPhotos = ids.Count == 0 ? [] : (await db.VisibleItems.Where(i => ids.Contains(i.Id) && i.MediaLevel == MediaLevel.Everything).Select(i => i.Id).ToListAsync(cancellationToken)).ToHashSet();

        Blocks = blocks.Select(b =>
        {
            if (b.Kind == StoryBlockKind.Text || b.ArchiveItemId is not { } itemId || !items.TryGetValue(itemId, out var item))
                return new BlockRow(b.Id, b.Kind, b.Text, b.Caption, b.ArchiveItemId, null, null, null, b.Kind == StoryBlockKind.Text ? ItemState.Shown : ItemState.Missing, null);

            var state = shown.Contains(itemId)
                ? shownWithPhotos.Contains(itemId) ? ItemState.Shown : ItemState.ShownWithoutPhotos
                : item.DeletedAt is not null ? ItemState.InBin
                : item.Status is ItemStatus.Rejected or ItemStatus.Withdrawn ? ItemState.Withdrawn
                : item.PublicFrom is not null && item.Status == ItemStatus.Published ? ItemState.PublicFrom
                : ItemState.Draft;
            return new BlockRow(b.Id, b.Kind, b.Text, b.Caption, itemId, item.Title, item.DateText, item.Thumbnail, state, item.PublicFrom);
        }).ToList();
    }

    /// <summary>The text with the line breaks of every system made one kind and the edges trimmed; null when nothing is left.</summary>
    private static string? Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    /// <summary>The number in "12", "/items/12", "/Admin/Items/Edit/12" or the same with the address of the site in front.</summary>
    public static int? ItemNumber(string? reference)
    {
        var match = ItemReference().Match(reference ?? "");
        return match.Success && int.TryParse(match.Groups["number"].Value, out var number) && number > 0 ? number : null;
    }

    [GeneratedRegex(@"^\s*(?:https?://[^/\s]+)?(?:/(?:items|admin/items/edit)/)?(?<number>\d{1,9})(?:[/?#]\S*)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ItemReference();
}
