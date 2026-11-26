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

    /// <summary>Files outside Data and Pages/Admin that name a person's list for another reason, each with the reason.</summary>
    private static readonly Dictionary<string, string> PeopleAllowed = new()
    {
        ["Export/ArchiveExporter.cs"] = "writes the export format's own list of people on an item (a list of numbers); the people are read in Data/PeopleExport.cs",
        ["Import/ExportImport.cs"] = "reads the export format's own list of people; the people are made in Data/PeopleImport.cs",
    };

    /// <summary>
    /// A person is public by <c>Visibility.Person</c> and <c>VisiblePeople</c> only (plan M30, test 5): no other file may query
    /// people, follow an item to its people, or follow a link to its person, because that would skip the rule.
    /// </summary>
    [Fact]
    public void Public_code_reads_people_only_through_the_visible_queries()
    {
        var offenders = WebFiles()
            .Where(f => !f.Relative.StartsWith("Data/") && !f.Relative.StartsWith("Pages/Admin/") && !PeopleAllowed.ContainsKey(f.Relative))
            .Where(f => PeopleForbidden().IsMatch(f.Text))
            .Select(f => f.Relative)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These files reach people without VisiblePeople. Use it, or list the file in PeopleAllowed with the reason:\n" + string.Join('\n', offenders));
    }

    /// <summary>The choice of who may see a person is named in Visibility, the entity and the admin pages, and nowhere else.</summary>
    [Fact]
    public void Only_visibility_the_entity_and_the_admin_pages_name_the_choice_of_who_may_see_a_person()
    {
        var offenders = WebFiles()
            .Where(f => f.Relative is not ("Data/Visibility.cs" or "Domain/Person.cs") && !f.Relative.StartsWith("Pages/Admin/") && !f.Relative.StartsWith("Data/Migrations/"))
            .Where(f => DisclosureForbidden().IsMatch(f.Text))
            .Select(f => f.Relative)
            .Where(r => r != "Data/CommonageDbContext.cs") // the column's mapping and its default
            .ToList();

        Assert.True(offenders.Count == 0, "These files name PersonDisclosure. Ask Visibility instead:\n" + string.Join('\n', offenders));
    }

    [GeneratedRegex(@"\bdb\.People\b|\bdb\.PersonNames\b|\bdb\.ItemPeople\b|(?<!AuditKind)\.People\b|\.Links\b|(?<!TagKind)\.Person\b")]
    private static partial Regex PeopleForbidden();

    [GeneratedRegex(@"\bPersonDisclosure\b")]
    private static partial Regex DisclosureForbidden();

    [Fact]
    public void The_people_guards_catch_the_ways_around_the_visible_queries()
    {
        foreach (var line in new[] { "db.People.Where(x)", "db.PersonNames.ToList()", "db.ItemPeople.Count()", "item.People.Select(l => l.PersonId)", "person.Links.Count", "l.Person.Name", "x => x.Person" })
            Assert.Matches(PeopleForbidden(), line);
        foreach (var line in new[] { "TagKind.Person", "AuditKind.People", "await VisiblePeople.ViewAsync(db, db.VisiblePeople, key, token)", "db.VisiblePeople.Where(x)", "Person.Clean(name)" })
            Assert.DoesNotMatch(PeopleForbidden(), line);
        Assert.Matches(DisclosureForbidden(), "p.Disclosure == PersonDisclosure.Deceased");
        Assert.DoesNotMatch(DisclosureForbidden(), "AuditAction.PersonDisclosureChanged");
    }

    private static IEnumerable<(string Relative, string Text)> WebFiles()
    {
        var root = SourceRoot();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith("obj/") || relative.StartsWith("bin/") || !(file.EndsWith(".cs") || file.EndsWith(".cshtml")))
                continue;
            yield return (relative, File.ReadAllText(file));
        }
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Commonage.Web")))
            directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Source folder not found."), "src", "Commonage.Web");
    }
}
