using System.Text.RegularExpressions;

namespace Commonage.Tests;

/// <summary>
/// A tripwire, not a proof (<see cref="VisibilityMatrixTests"/> proves the behaviour): code a visitor reaches must not read
/// items by another way than <c>VisibleItems</c> and <c>VisibleMedia</c>. Needs no Docker.
/// </summary>
public partial class VisibilityGuardTests
{
    /// <summary>What a visitor's request may run: everything under these, except what the list below names.</summary>
    private static readonly string[] PublicFolders = ["Pages", "Search", "Media", "Export"];

    /// <summary>Files that read items another way on purpose, each with its reason.</summary>
    private static readonly Dictionary<string, string> Allowed = new()
    {
        ["Media/MediaEndpoints.cs"] = "the staff path (/admin/media) reads every file for curators",
        ["Media/MediaProcessing.cs"] = "makes the previews of files nobody may see yet",
        ["Media/FileTextIndex.cs"] = "keeps the search text of an item's PDFs in step, for staff and background work",
        ["Search/AdminItemList.cs"] = "the curators' list, which sees drafts",
        ["Media/MediaUploadService.cs"] = "the position of the next file counts files in the bin too",
        ["Pages/Contribute/Index.cshtml.cs"] = "the contributor's own submissions, found by their link",
        ["Export/ArchiveExporter.cs"] = "the full export is for admins; the published-only copy goes through VisibleItems",
    };

    [Fact]
    public void Public_code_reads_items_only_through_the_visible_queries()
    {
        var root = SourceRoot();
        var offenders = new List<string>();
        foreach (var folder in PublicFolders)
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.StartsWith("Pages/Admin/") || Allowed.ContainsKey(relative))
                    continue;
                if (!file.EndsWith(".cs") && !file.EndsWith(".cshtml"))
                    continue;
                if (Forbidden().IsMatch(File.ReadAllText(file)))
                    offenders.Add(relative);
            }

        Assert.True(offenders.Count == 0,
            "These files read items without VisibleItems or VisibleMedia. Use them, or list the file in Allowed with the reason:\n"
            + string.Join('\n', offenders));
    }

    [GeneratedRegex(@"ItemStatus\.Published|\bArchiveItems\b|IgnoreQueryFilters|\bdb\.MediaFiles\b|StoryStatus\.Published|\bdb\.Stories\b|\bdb\.StoryBlocks\b|\bstory\.Blocks\b")]
    private static partial Regex Forbidden();

    /// <summary>
    /// A story page must not follow a block to its item: that would show an item the rule hides. The blocks come from
    /// <c>VisibleStoryBlocks</c>, which loads the items through <c>VisibleItems</c>.
    /// </summary>
    [Fact]
    public void Public_story_pages_never_follow_a_block_to_its_item()
    {
        var root = SourceRoot();
        var offenders = new List<string>();
        foreach (var folder in new[] { "Pages/Stories", "Pages/Index.cshtml", "Pages/Index.cshtml.cs" })
        {
            var path = Path.Combine(root, folder);
            var files = Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories) : File.Exists(path) ? [path] : [];
            offenders.AddRange(files.Where(f => FollowsBlockToItem().IsMatch(File.ReadAllText(f))).Select(f => Path.GetRelativePath(root, f)));
        }

        Assert.Empty(offenders);
    }

    [GeneratedRegex(@"\.ArchiveItem")]
    private static partial Regex FollowsBlockToItem();

    [Fact]
    public void The_guard_catches_the_ways_to_reach_a_story_or_a_block_around_the_visible_queries()
    {
        foreach (var line in new[] { "s.Status == StoryStatus.Published", "db.Stories.Where(x)", "db.StoryBlocks.ToList()", "story.Blocks.Select(b => 1)" })
            Assert.Matches(Forbidden(), line);
        Assert.DoesNotMatch(Forbidden(), "await VisibleStoryBlocks.LoadAsync(db, id)");
        Assert.DoesNotMatch(Forbidden(), "db.VisibleStories.Where(x)");
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Commonage.Web")))
            directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Source folder not found."), "src", "Commonage.Web");
    }
}
