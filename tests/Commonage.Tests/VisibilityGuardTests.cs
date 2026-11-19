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

    [GeneratedRegex(@"ItemStatus\.Published|\bArchiveItems\b|IgnoreQueryFilters|\bdb\.MediaFiles\b")]
    private static partial Regex Forbidden();

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Commonage.Web")))
            directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Source folder not found."), "src", "Commonage.Web");
    }
}
